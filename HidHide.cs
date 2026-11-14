using System.Diagnostics;
using Microsoft.Win32;

namespace PadSwap;

/// <summary>
/// Drives the HidHide filter driver through its command line client. HidHide is what keeps games
/// from seeing the physical controller, so they only get the remapped virtual one.
/// </summary>
internal static class HidHide
{
    private const string RegistryKey = @"SOFTWARE\Nefarius Software Solutions e.U.\HidHide";
    private const string DefaultInstallDirectory = @"C:\Program Files\Nefarius Software Solutions\HidHide";

    public static string? CliPath { get; } = FindCli();

    public static bool IsInstalled => CliPath != null;

    /// <summary>Allows this process to keep seeing hidden devices, hides the given devices and turns hiding on.</summary>
    public static bool Hide(IEnumerable<string> deviceInstanceIds)
    {
        var arguments = new List<string> { "--app-reg", Environment.ProcessPath! };
        foreach (string id in deviceInstanceIds)
        {
            arguments.Add("--dev-hide");
            arguments.Add(id);
        }
        arguments.Add("--cloak-on");
        return Run(arguments);
    }

    public static bool Unhide(IEnumerable<string> deviceInstanceIds)
    {
        var arguments = new List<string>();
        foreach (string id in deviceInstanceIds)
        {
            arguments.Add("--dev-unhide");
            arguments.Add(id);
        }
        return arguments.Count == 0 || Run(arguments);
    }

    private static bool Run(List<string> arguments)
    {
        if (CliPath == null) return false;
        try
        {
            var startInfo = new ProcessStartInfo(CliPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                Log.Write("HidHideCLI timed out.");
                return false;
            }
            if (process.ExitCode != 0)
            {
                Log.Write($"HidHideCLI {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {output.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            Log.Write($"Could not run HidHideCLI: {exception.Message}");
            return false;
        }
    }

    private static string? FindCli()
    {
        var directories = new List<string>();
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKey))
        {
            if (key?.GetValue("Path") is string installed)
                directories.Add(installed);
        }
        directories.Add(DefaultInstallDirectory);

        foreach (string directory in directories)
        {
            foreach (string relative in new[] { @"x64\HidHideCLI.exe", "HidHideCLI.exe" })
            {
                string candidate = Path.Combine(directory, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
