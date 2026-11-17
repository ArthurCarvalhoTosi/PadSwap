using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PadSwap;

/// <summary>
/// Talks to an Xbox 360 class controller (XUSB) directly through its driver, the same way
/// XInput does internally. Going around the XInput API lets us pick one exact physical device
/// by path, so the virtual controller we create can never be mistaken for the physical one.
/// </summary>
internal sealed unsafe class XusbDevice : IPhysicalPad
{
    public static readonly Guid InterfaceClass = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    private const int StateSize = 29;

    // Offset of the gamepad block inside the state buffer: wButtons (2), triggers (2), four stick axes (8).
    private const int GamepadOffset = 11;
    private const int GamepadSize = 12;

    private const uint IoctlGetInformation = 0x80006000;
    private const uint IoctlGetState = 0x8000E00C;
    private const uint IoctlSetState = 0x8000A010;
    private const uint IoctlGetLedState = 0x8000E008;

    private const byte SetStateLed = 0x01;
    private const byte SetStateVibration = 0x02;

    private readonly SafeFileHandle _handle;
    private readonly HighResolutionTimer _pollTimer = new(periodMilliseconds: 1);
    private volatile bool _interrupted;

    public PadKind Kind => PadKind.Xbox;
    public string InstanceId { get; }
    public string RootInstanceId => InstanceId;

    private XusbDevice(string interfacePath, SafeFileHandle handle)
    {
        InstanceId = DeviceTree.InstanceIdFromInterfacePath(interfacePath);
        _handle = handle;
    }

    public static List<string> FindPhysicalInterfacePaths() =>
        DeviceTree.GetInterfacePaths(InterfaceClass)
            .Where(path => !PhysicalPads.IsVirtual(DeviceTree.InstanceIdFromInterfacePath(path)))
            .ToList();

    /// <summary>Opens the device. Throws <see cref="Win32Exception"/> when access is denied or it is gone.</summary>
    public static XusbDevice Open(string interfacePath) =>
        new(interfacePath, Native.OpenDevice(interfacePath, Native.GenericRead | Native.GenericWrite));

    /// <summary>Reads the driver information block (version, vendor id, product id). Used for diagnostics.</summary>
    public byte[] ReadInformation()
    {
        var output = new byte[12];
        fixed (byte* outputPointer = output)
        {
            if (!Native.DeviceIoControl(_handle, IoctlGetInformation, null, 0, outputPointer, (uint)output.Length, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return output;
    }

    /// <summary>The driver has no "wait for input" call we can rely on, so the state is polled every millisecond.</summary>
    public bool TryReadNext(out PadState state)
    {
        _pollTimer.Wait();
        if (_interrupted)
        {
            state = default;
            return false;
        }

        // Protocol version 0x0101 followed by the device index on the bus (always 0 for wired pads).
        byte* input = stackalloc byte[3] { 0x01, 0x01, 0x00 };
        byte* output = stackalloc byte[StateSize];
        if (!Native.DeviceIoControl(_handle, IoctlGetState, input, 3, output, StateSize, out uint returned, IntPtr.Zero)
            || returned < GamepadOffset + GamepadSize)
        {
            state = default;
            return false;
        }

        var gamepad = new ReadOnlySpan<byte>(output + GamepadOffset, GamepadSize);
        state = new PadState(
            BitConverter.ToUInt16(gamepad), gamepad[2], gamepad[3],
            BitConverter.ToInt16(gamepad[4..]), BitConverter.ToInt16(gamepad[6..]),
            BitConverter.ToInt16(gamepad[8..]), BitConverter.ToInt16(gamepad[10..]));
        return true;
    }

    // No I/O to cancel: TryReadNext never waits longer than one poll period.
    public void Interrupt() => _interrupted = true;

    public byte ReadLed() => ReadLed(_handle);

    public void SetLed(byte led) => SetLed(_handle, led);

    /// <summary>
    /// Player LED pattern (6..9 = player 1..4 lit). XInput derives the user index, the "slot"
    /// games ask for, from this value.
    /// </summary>
    public static byte ReadLed(SafeFileHandle handle)
    {
        byte* input = stackalloc byte[3] { 0x01, 0x01, 0x00 };
        byte* output = stackalloc byte[3];
        if (!Native.DeviceIoControl(handle, IoctlGetLedState, input, 3, output, 3, out uint returned, IntPtr.Zero) || returned < 3)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return output[2];
    }

    public static void SetLed(SafeFileHandle handle, byte led)
    {
        byte* input = stackalloc byte[5] { 0x00, led, 0x00, 0x00, SetStateLed };
        if (!Native.DeviceIoControl(handle, IoctlSetState, input, 5, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void SetVibration(byte largeMotor, byte smallMotor)
    {
        byte* input = stackalloc byte[5] { 0x00, 0x00, largeMotor, smallMotor, SetStateVibration };
        Native.DeviceIoControl(_handle, IoctlSetState, input, 5, null, 0, out _, IntPtr.Zero);
    }

    public void Dispose()
    {
        _handle.Dispose();
        _pollTimer.Dispose();
    }
}
