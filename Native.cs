using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PadSwap;

internal static unsafe class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;

    /// <summary>Opens a device path with shared access. Throws <see cref="Win32Exception"/> on failure.</summary>
    public static SafeFileHandle OpenDevice(string path, uint access)
    {
        const uint shareReadWrite = 0x00000003;
        const uint openExisting = 3;
        const uint attributeNormal = 0x80;

        SafeFileHandle handle = CreateFileW(path, access, shareReadWrite, IntPtr.Zero, openExisting, attributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte* input, uint inputSize,
        byte* output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(SafeFileHandle file, byte* buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteFile(SafeFileHandle file, byte* buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
}

/// <summary>
/// Periodic 1 ms class timer. Thread.Sleep(1) rounds up to ~15 ms unless the global timer
/// resolution is raised, and Windows 11 ignores that request for processes without a visible
/// window, so a high resolution waitable timer is used instead.
/// </summary>
internal sealed class HighResolutionTimer : IDisposable
{
    private const uint CreateHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly IntPtr _handle;

    public HighResolutionTimer(int periodMilliseconds)
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateHighResolution, TimerAllAccess);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        long dueTime = -10_000L * periodMilliseconds; // relative, in 100 ns units
        if (!SetWaitableTimer(_handle, in dueTime, periodMilliseconds, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Wait() => WaitForSingleObject(_handle, Infinite);

    public void Dispose() => CloseHandle(_handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, in long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
