using System.Runtime;
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

    private IPhysicalPad? _pad;
    private List<string> _hiddenInstanceIds = [];

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
        ButtonMap map = ButtonMap.Load(pad.Kind);
        IXbox360Controller virtualPad = client.CreateXbox360Controller();
        virtualPad.AutoSubmitReport = false;
        virtualPad.FeedbackReceived += OnFeedbackReceived;
        virtualPad.Connect();
        lock (_padGate) _pad = pad;
        string mode = pad.Kind == PadKind.Xbox ? "modo Xbox" : "modo PlayStation";
        Report(RemapperState.Active, HidHide.IsInstalled ? $"Ativo ({mode})" : $"Ativo ({mode}), sem HidHide: entrada duplicada");

        try
        {
            PadState previous = default;
            bool first = true;

            while (!_stopRequested.IsSet && pad.TryReadNext(out PadState state))
            {
                if (!first && state == previous) continue;
                first = false;
                previous = state;

                virtualPad.SetButtonsFull(map.Apply(state.Buttons));
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
            pad.SetVibration(0, 0);
            virtualPad.FeedbackReceived -= OnFeedbackReceived;
            try { virtualPad.Disconnect(); }
            catch (Exception exception) { Log.Write($"Virtual controller disconnect failed: {exception.Message}"); }
        }
    }

    /// <summary>Games send rumble to the virtual controller; pass it on to the physical one.</summary>
    private void OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
    {
        lock (_padGate) _pad?.SetVibration(e.LargeMotor, e.SmallMotor);
    }

    private void Report(RemapperState state, string text) => StateChanged?.Invoke(state, text);
}
