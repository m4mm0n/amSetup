// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace AmSetup;

internal static class PrerequisiteDetector
{
    public static bool IsSatisfied(SetupPrerequisite prerequisite)
    {
        if (!string.IsNullOrWhiteSpace(prerequisite.DetectCommand))
            return RunDetectCommand(prerequisite);

        return prerequisite.Kind.ToLowerInvariant() switch
        {
            "dotnet-runtime" => DotNetRuntimeInstalled(prerequisite.Version, runtimeName: null),
            "dotnet-desktop-runtime" => DotNetRuntimeInstalled(prerequisite.Version, "Microsoft.WindowsDesktop.App"),
            "aspnet-runtime" => DotNetRuntimeInstalled(prerequisite.Version, "Microsoft.AspNetCore.App"),
            "netfx" => NetFrameworkInstalled(prerequisite.Version),
            "vc-redist" => VcRuntimeInstalled(),
            _ => false
        };
    }

    private static bool RunDetectCommand(SetupPrerequisite prerequisite)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(prerequisite.DetectCommand, prerequisite.DetectArguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool DotNetRuntimeInstalled(string version, string? runtimeName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", "--list-runtimes")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null) return false;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            string major = Major(version);
            return output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Any(line =>
                    (runtimeName is null || line.StartsWith(runtimeName, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(major) || line.Contains(" " + major + ".", StringComparison.Ordinal)));
        }
        catch
        {
            return false;
        }
    }

    private static bool NetFrameworkInstalled(string version)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrWhiteSpace(version)) return false;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            int release = Convert.ToInt32(key?.GetValue("Release") ?? 0);
            return release >= NetFxRelease(version);
        }
        catch
        {
            return false;
        }
    }

    private static bool VcRuntimeInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(system, "vcruntime140.dll")) ||
            File.Exists(Path.Combine(system, "vcruntime140_1.dll"));
    }

    private static string Major(string version)
    {
        int dot = version.IndexOf('.');
        return dot < 0 ? version.Trim() : version[..dot].Trim();
    }

    private static int NetFxRelease(string version) => version.Trim() switch
    {
        "4.8.1" => 533320,
        "4.8" => 528040,
        "4.7.2" => 461808,
        "4.7.1" => 461308,
        "4.7" => 460798,
        "4.6.2" => 394802,
        "4.6.1" => 394254,
        "4.6" => 393295,
        _ => 528040
    };
}
