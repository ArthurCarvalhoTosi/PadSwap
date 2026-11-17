using System.ComponentModel;
using System.Runtime;
using Microsoft.Win32.SafeHandles;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace PadSwap;

internal enum RemapperState
{
    WaitingForController,
    Active,
    Error,
}

/// <summary>
/// The core loop: reads the physical controller, applies the button map and feeds a virtual
/// Xbox 360 controller. The physical one is hidden from every other process through HidHide.
/// </summary>
internal sealed class Remapper
{
    private const int ReconnectDelayMilliseconds = 1000;

    private readonly ManualResetEventSlim _stopRequested = new(false);
    private readonly Thread _thread;
    private readonly object _padGate = new();

    private readonly object _snapshotGate = new();

    private IPhysicalPad? _pad;
    private List<string> _hiddenInstanceIds = [];
    private volatile ButtonMap _map = ButtonMap.Load(PadKind.Xbox);
    private volatile bool _mapChanged;
    private (PadState Physical, ushort VirtualButtons) _snapshot;

    /// <summary>Personality of the controller being forwarded, or null while none is connected.</summary>
    public PadKind? CurrentKind
    {
        get { lock (_padGate) return _pad?.Kind; }
    }

    /// <summary>Latest physical state and the buttons sent to the virtual controller, for the live view.</summary>
    public (PadState Physical, ushort VirtualButtons) Snapshot
    {
        get { lock (_snapshotGate) return _snapshot; }
    }

    /// <summary>Applies an edited padswap.json without reconnecting the controller.</summary>
    public void ReloadMap()
    {
        if (CurrentKind is not PadKind kind) return;
        _map = ButtonMap.Load(kind);
        _mapChanged = true;
    }

    public event Action<RemapperState, string>? StateChanged;

    public Remapper()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PadSwap remapper", Priority = ThreadPriority.Highest };
    }

    public void Start() => _thread.Start();

    /// <summary>Stops the loop, removes the virtual controller and makes the physical one visible again.</summary>
    public void Stop()
    {
        _stopRequested.Set();
        lock (_padGate) _pad?.Interrupt();
        _thread.Join(3000);
        HidHide.Unhide(_hiddenInstanceIds);
    }

    private void Run()
    {
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            using var client = new ViGEmClient();

            if (!HidHide.IsInstalled)
                Log.Write("HidHide is not installed: games will see both the physical and the virtual controller.");

            while (!_stopRequested.IsSet)
            {
                IPhysicalPad? pad = PhysicalPads.OpenFirst(beforeOpen: Hide);
                if (pad == null)
                {
                    Report(RemapperState.WaitingForController, "Aguardando o controle");
                    _stopRequested.Wait(ReconnectDelayMilliseconds);
                    continue;
                }

                using (pad)
                {
                    Log.Write($"Physical controller opened in {pad.Kind} mode: {pad.InstanceId}");
                    Forward(client, pad);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Write($"Remapper stopped: {exception}");
            // Without the virtual controller the hidden physical one would leave the user with no controller at all.
            HidHide.Unhide(_hiddenInstanceIds);
            Report(RemapperState.Error, exception is VigemBusNotFoundException
                ? "Driver ViGEmBus não encontrado"
                : "Erro, veja o log");
        }
    }

    private void Hide(IEnumerable<string> instanceIds)
    {
        List<string> nodes = instanceIds.ToList();
        if (HidHide.Hide(nodes))
            _hiddenInstanceIds = nodes;
    }

    private void Forward(ViGEmClient client, IPhysicalPad pad)
    {
        _map = ButtonMap.Load(pad.Kind);
        IXbox360Controller virtualPad = client.CreateXbox360Controller();
        virtualPad.AutoSubmitReport = false;
        virtualPad.FeedbackReceived += OnFeedbackReceived;
        HashSet<string> xusbPathsBefore = DeviceTree.GetInterfacePaths(XusbDevice.InterfaceClass).ToHashSet();
        virtualPad.Connect();
        byte? physicalLed = pad is XusbDevice xusb ? TakeOverPlayerSlot(xusb, xusbPathsBefore) : null;
        lock (_padGate) _pad = pad;
        string mode = pad.Kind == PadKind.Xbox ? "modo Xbox" : "modo PlayStation";
        Report(RemapperState.Active, HidHide.IsInstalled ? $"Ativo ({mode})" : $"Ativo ({mode}), sem HidHide: entrada duplicada");

        try
        {
            PadState previous = default;
            bool first = true;

            while (!_stopRequested.IsSet && pad.TryReadNext(out PadState state))
            {
                if (!first && !_mapChanged && state == previous) continue;
                first = false;
                _mapChanged = false;
                previous = state;

                ushort virtualButtons = _map.Apply(state.Buttons);
                lock (_snapshotGate) _snapshot = (state, virtualButtons);
                virtualPad.SetButtonsFull(virtualButtons);
                virtualPad.SetSliderValue(Xbox360Slider.LeftTrigger, state.LeftTrigger);
                virtualPad.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);
                virtualPad.SetAxisValue(Xbox360Axis.LeftThumbX, state.LeftX);
                virtualPad.SetAxisValue(Xbox360Axis.LeftThumbY, state.LeftY);
                virtualPad.SetAxisValue(Xbox360Axis.RightThumbX, state.RightX);
                virtualPad.SetAxisValue(Xbox360Axis.RightThumbY, state.RightY);
                virtualPad.SubmitReport();
            }

            if (!_stopRequested.IsSet)
                Log.Write("Physical controller lost.");
        }
        finally
        {
            lock (_padGate) _pad = null;
            lock (_snapshotGate) _snapshot = default;
            pad.SetVibration(0, 0);
            if (physicalLed is byte led)
            {
                // Hand the slot back, so the physical pad is player 1 again once PadSwap exits.
                try { ((XusbDevice)pad).SetLed(led); }
                catch (Win32Exception) { /* Already unplugged. */ }
            }
            virtualPad.FeedbackReceived -= OnFeedbackReceived;
            try { virtualPad.Disconnect(); }
            catch (Exception exception) { Log.Write($"Virtual controller disconnect failed: {exception.Message}"); }
        }
    }

    /// <summary>
    /// xusb22 hands out XInput slots in arrival order, so a physical pad plugged in since boot
    /// holds slot 0 and the virtual one lands in slot 1, which games that only read the first
    /// slot never see. XInput takes the slot from the player LED, so swapping the two LEDs moves
    /// the virtual pad into the physical one's slot. Returns the physical LED to restore later.
    /// </summary>
    private static byte? TakeOverPlayerSlot(XusbDevice physical, HashSet<string> xusbPathsBefore)
    {
        const int attempts = 30;
        const int delayMilliseconds = 100;

        try
        {
            // The virtual pad's interface and LED show up shortly after Connect returns.
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                string? virtualPath = DeviceTree.GetInterfacePaths(XusbDevice.InterfaceClass)
                    .FirstOrDefault(path => !xusbPathsBefore.Contains(path));
                if (virtualPath != null)
                {
                    using SafeFileHandle virtualHandle = Native.OpenDevice(virtualPath, Native.GenericRead | Native.GenericWrite);
                    byte virtualLed = XusbDevice.ReadLed(virtualHandle);
                    if (virtualLed != 0)
                    {
                        byte physicalLed = physical.ReadLed();
                        if (virtualLed != physicalLed)
                        {
                            physical.SetLed(virtualLed);
                            XusbDevice.SetLed(virtualHandle, physicalLed);
                            Log.Write($"Virtual controller moved to the physical one's player slot (LED {virtualLed} -> {physicalLed}).");
                        }
                        return physicalLed;
                    }
                }
                Thread.Sleep(delayMilliseconds);
            }
            Log.Write("Virtual controller did not show up in time; it keeps the player slot Windows gave it.");
        }
        catch (Win32Exception exception)
        {
            Log.Write($"Could not swap player slots: {exception.Message}");
        }
        return null;
    }

    /// <summary>Games send rumble to the virtual controller; pass it on to the physical one.</summary>
    private void OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
    {
        lock (_padGate) _pad?.SetVibration(e.LargeMotor, e.SmallMotor);
    }

    private void Report(RemapperState state, string text) => StateChanged?.Invoke(state, text);
}
