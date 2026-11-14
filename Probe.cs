using System.Runtime.InteropServices;

namespace PadSwap;

/// <summary>
/// Diagnostic modes, printing to standard output:
///   --probe [seconds]   lists the controllers found and prints every state change.
///   --learn [seconds]   prints the face buttons in the order they are pressed, to find out
///                       which physical position sends which button.
///   --xinput            prints what an ordinary game sees through the public XInput API.
/// </summary>
internal static class Probe
{
    private const PadButton FaceButtons = PadButton.A | PadButton.B | PadButton.X | PadButton.Y;

    public static int Run(string[] args)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out int parsed) ? parsed : 3;
        return args[0] switch
        {
            "--xinput" => PrintXInputSlots(),
            "--learn" => Learn(seconds),
            _ => Watch(seconds, rumble: args.Contains("--rumble")),
        };
    }

    private static int PrintXInputSlots()
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            uint error = XInputGetStateEx(slot, out XInputState state);
            Console.WriteLine(error == 0
                ? $"slot {slot}: buttons=0x{state.Buttons:X4} ({(PadButton)state.Buttons}) LT={state.LeftTrigger} RT={state.RightTrigger} LX={state.ThumbLX} LY={state.ThumbLY} RX={state.ThumbRX} RY={state.ThumbRY}"
                : $"slot {slot}: not connected");
        }
        return 0;
    }

    private static int Watch(int seconds, bool rumble)
    {
        Console.WriteLine("Xbox-mode controllers (XUSB):");
        foreach (string path in DeviceTree.GetInterfacePaths(XusbDevice.InterfaceClass))
            Describe(path);
        Console.WriteLine("PlayStation-mode controllers (HID):");
        foreach (string path in Ds4Device.FindPhysicalInterfacePaths())
            Describe(path);
        PrintXInputSlots();

        using IPhysicalPad? pad = PhysicalPads.OpenFirst();
        if (pad == null)
        {
            Console.WriteLine("No physical controller found.");
            return 1;
        }
        Console.WriteLine($"Opened {pad.Kind}: {pad.InstanceId} (root {pad.RootInstanceId})");
        if (pad is XusbDevice xusb)
            Console.WriteLine($"Information: {Convert.ToHexString(xusb.ReadInformation())}");

        if (rumble)
        {
            pad.SetVibration(0, 160);
            Thread.Sleep(300);
            pad.SetVibration(0, 0);
            Console.WriteLine("Rumble pulse sent.");
        }

        StopAfter(pad, seconds);
        PadState previous = default;
        int reads = 0;
        while (pad.TryReadNext(out PadState state))
        {
            if (reads++ > 0 && state == previous) continue;
            previous = state;
            if (pad is Ds4Device ds4)
                Console.WriteLine($"  raw={Convert.ToHexString(ds4.LastReport)}");
            Console.WriteLine($"  buttons=0x{state.Buttons:X4} ({(PadButton)state.Buttons}) LT={state.LeftTrigger} RT={state.RightTrigger} " +
                $"LX={state.LeftX} LY={state.LeftY} RX={state.RightX} RY={state.RightY}");
        }
        Console.WriteLine($"{reads} reads in {seconds}s ({reads / Math.Max(seconds, 1)} per second).");
        return 0;
    }

    private static int Learn(int seconds)
    {
        using IPhysicalPad? pad = PhysicalPads.OpenFirst();
        if (pad == null)
        {
            Console.WriteLine("No physical controller found.");
            return 1;
        }
        Console.WriteLine($"Opened {pad.Kind}: {pad.InstanceId}");

        StopAfter(pad, seconds);
        var order = new List<PadButton>();
        PadButton previous = 0;
        while (order.Count < 4 && pad.TryReadNext(out PadState state))
        {
            PadButton face = (PadButton)state.Buttons & FaceButtons;
            PadButton pressed = face & ~previous;
            previous = face;
            foreach (PadButton button in new[] { PadButton.A, PadButton.B, PadButton.X, PadButton.Y })
            {
                if ((pressed & button) != 0 && !order.Contains(button))
                    order.Add(button);
            }
        }
        Console.WriteLine($"Press order: {string.Join(", ", order)}");
        return order.Count == 4 ? 0 : 2;
    }

    private static void Describe(string interfacePath)
    {
        string instanceId = DeviceTree.InstanceIdFromInterfacePath(interfacePath);
        Console.WriteLine($"  {instanceId}");
        Console.WriteLine($"    parent : {DeviceTree.GetParentInstanceId(instanceId)}");
        Console.WriteLine($"    virtual: {PhysicalPads.IsVirtual(instanceId)}");
    }

    private static void StopAfter(IPhysicalPad pad, int seconds) =>
        Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => pad.Interrupt());

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    // Ordinal 100 is the undocumented XInputGetStateEx, which also reports the Guide button.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint userIndex, out XInputState state);
}
