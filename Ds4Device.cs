using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace PadSwap;

/// <summary>
/// Reads a wired DualShock 4 compatible controller through raw HID reports and translates them
/// to Xbox terms by position (Cross = A, Circle = B, Square = X, Triangle = Y).
/// </summary>
internal sealed unsafe class Ds4Device : IPhysicalPad
{
    private static readonly Guid HidInterfaceClass = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static readonly string[] SupportedHardwareIds = ["VID_054C&PID_05C4", "VID_054C&PID_09CC"];

    private const int ReadBufferSize = 256;
    private const byte UsbInputReportId = 0x01;
    private const int MinimumInputReportSize = 10;

    private const byte UsbOutputReportId = 0x05;
    private const int OutputReportSize = 32;
    private const byte OutputFlagRumble = 0x01;

    // Hat switch value (0 = up, clockwise, 8 = released) to d-pad button bits.
    private static readonly PadButton[] HatToDpad =
    [
        PadButton.DpadUp,
        PadButton.DpadUp | PadButton.DpadRight,
        PadButton.DpadRight,
        PadButton.DpadDown | PadButton.DpadRight,
        PadButton.DpadDown,
        PadButton.DpadDown | PadButton.DpadLeft,
        PadButton.DpadLeft,
        PadButton.DpadUp | PadButton.DpadLeft,
    ];

    private readonly SafeFileHandle _readHandle;

    // Separate handle for output: on a synchronous handle a write would queue behind the pending read.
    private readonly SafeFileHandle? _writeHandle;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private volatile bool _interrupted;

    public PadKind Kind => PadKind.PlayStation;
    public string InstanceId { get; }
    public string RootInstanceId { get; }

    /// <summary>Last raw input report, for diagnostics.</summary>
    public ReadOnlySpan<byte> LastReport => _readBuffer.AsSpan(0, 16);

    private Ds4Device(string interfacePath, string rootInstanceId, SafeFileHandle readHandle, SafeFileHandle? writeHandle)
    {
        InstanceId = DeviceTree.InstanceIdFromInterfacePath(interfacePath);
        RootInstanceId = rootInstanceId;
        _readHandle = readHandle;
        _writeHandle = writeHandle;
    }

    public static List<string> FindPhysicalInterfacePaths() =>
        DeviceTree.GetInterfacePaths(HidInterfaceClass)
            .Where(path => SupportedHardwareIds.Any(id => path.Contains(id, StringComparison.OrdinalIgnoreCase)))
            .Where(path => !PhysicalPads.IsVirtual(DeviceTree.InstanceIdFromInterfacePath(path)))
            .ToList();

    public static Ds4Device Open(string interfacePath, string rootInstanceId)
    {
        SafeFileHandle readHandle = Native.OpenDevice(interfacePath, Native.GenericRead);
        SafeFileHandle? writeHandle = null;
        try
        {
            writeHandle = Native.OpenDevice(interfacePath, Native.GenericWrite);
        }
        catch (Win32Exception exception)
        {
            Log.Write($"No rumble: the controller could not be opened for output ({exception.Message}).");
        }
        return new Ds4Device(interfacePath, rootInstanceId, readHandle, writeHandle);
    }

    /// <summary>Blocks until the controller sends its next report.</summary>
    public bool TryReadNext(out PadState state)
    {
        state = default;
        fixed (byte* buffer = _readBuffer)
        {
            while (!_interrupted)
            {
                if (!Native.ReadFile(_readHandle, buffer, ReadBufferSize, out uint read, IntPtr.Zero))
                    return false;
                if (read < MinimumInputReportSize || buffer[0] != UsbInputReportId)
                    continue;

                state = Translate(buffer);
                return true;
            }
        }
        return false;
    }

    private static PadState Translate(byte* report)
    {
        byte faceAndHat = report[5];
        byte shoulders = report[6];
        byte system = report[7];

        PadButton buttons = 0;
        int hat = faceAndHat & 0x0F;
        if (hat < HatToDpad.Length) buttons |= HatToDpad[hat];

        if ((faceAndHat & 0x10) != 0) buttons |= PadButton.X;             // Square
        if ((faceAndHat & 0x20) != 0) buttons |= PadButton.A;             // Cross
        if ((faceAndHat & 0x40) != 0) buttons |= PadButton.B;             // Circle
        if ((faceAndHat & 0x80) != 0) buttons |= PadButton.Y;             // Triangle
        if ((shoulders & 0x01) != 0) buttons |= PadButton.LeftShoulder;   // L1
        if ((shoulders & 0x02) != 0) buttons |= PadButton.RightShoulder;  // R1
        if ((shoulders & 0x10) != 0) buttons |= PadButton.Back;           // Share
        if ((shoulders & 0x20) != 0) buttons |= PadButton.Start;          // Options
        if ((shoulders & 0x40) != 0) buttons |= PadButton.LeftThumb;      // L3
        if ((shoulders & 0x80) != 0) buttons |= PadButton.RightThumb;     // R3
        if ((system & 0x01) != 0) buttons |= PadButton.Guide;             // PS

        // L2/R2 also have digital bits in the report; the analog values carry the same information.
        return new PadState((ushort)buttons, report[8], report[9],
            AxisFromByte(report[1]), InvertedAxisFromByte(report[2]),
            AxisFromByte(report[3]), InvertedAxisFromByte(report[4]));
    }

    // 0..255 to -32768..32767, keeping the resting value 128 at exactly 0.
    private static short AxisFromByte(byte value) =>
        (short)(value >= 128 ? (value - 128) * 32767 / 127 : (value - 128) * 256);

    // DualShock Y grows downwards, Xbox Y grows upwards.
    private static short InvertedAxisFromByte(byte value) => (short)Math.Min(-AxisFromByte(value), short.MaxValue);

    public void Interrupt()
    {
        _interrupted = true;
        Native.CancelIoEx(_readHandle, IntPtr.Zero);
    }

    public void SetVibration(byte largeMotor, byte smallMotor)
    {
        if (_writeHandle == null) return;

        byte* report = stackalloc byte[OutputReportSize];
        new Span<byte>(report, OutputReportSize).Clear();
        report[0] = UsbOutputReportId;
        report[1] = OutputFlagRumble;
        report[4] = smallMotor;
        report[5] = largeMotor;
        Native.WriteFile(_writeHandle, report, OutputReportSize, out _, IntPtr.Zero);
    }

    public void Dispose()
    {
        _readHandle.Dispose();
        _writeHandle?.Dispose();
    }
}
