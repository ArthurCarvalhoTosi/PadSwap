using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PadSwap;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--probe" or "--learn" or "--xinput")
            return Probe.Run(args);

        using var singleInstance = new Mutex(initiallyOwned: true, "PadSwap.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
            return 0;

        KeepFullSpeedInBackground();
        Log.Write($"PadSwap started from {Environment.ProcessPath}");

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
        return 0;
    }

    /// <summary>
    /// A tray-only process is a candidate for Windows 11 background throttling (efficiency mode,
    /// coarse timers), which would add input lag. Opt out and raise the priority.
    /// </summary>
    private static void KeepFullSpeedInBackground()
    {
        const int processPowerThrottling = 4;
        const uint executionSpeed = 0x1;
        const uint ignoreTimerResolution = 0x4;

        var throttling = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = executionSpeed | ignoreTimerResolution,
            StateMask = 0,
        };
        using Process process = Process.GetCurrentProcess();
        SetProcessInformation(process.Handle, processPowerThrottling, ref throttling, (uint)Marshal.SizeOf<PowerThrottlingState>());
        process.PriorityClass = ProcessPriorityClass.High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);
}
