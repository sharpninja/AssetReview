using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpNinja.AssetReview.Launcher;

/// <summary>
/// Starts the platform-specific AssetReview application next to the global .NET tool launcher.
/// 2026-11-23: FR-ASSETREVIEW-020 and TR-ASSETREVIEW-PACKAGING-001, AC-environment-dispatch.
/// Use the active .NET installation and forward arguments, working directory, and exit status.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        string? target = ResolveTarget();
        if (target is null)
        {
            Console.Error.WriteLine($"asset-review has no application for {RuntimeInformation.RuntimeIdentifier}. This package supports Windows and glibc-based Linux on x64 and ARM64.");
            return 1;
        }

        string application = Path.Combine(AppContext.BaseDirectory, "apps", target, "AssetReview.Tool.dll");
        if (!File.Exists(application))
        {
            Console.Error.WriteLine($"asset-review is missing its {target} application. Reinstall SharpNinja.AssetReview.Tool.");
            return 1;
        }

        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var startInfo = new ProcessStartInfo(dotnet) { UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(application);
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                Console.Error.WriteLine($"asset-review could not start its {target} application.");
                return 1;
            }
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine($"asset-review could not start its {target} application: {exception.Message}");
            return 1;
        }

        // Both processes receive console interrupts. Let the application finish its shutdown and return its status.
        ConsoleCancelEventHandler waitForChild = (_, interrupt) => interrupt.Cancel = true;
        EventHandler terminateChild = (_, _) => StopChild(process);
        Console.CancelKeyPress += waitForChild;
        AppDomain.CurrentDomain.ProcessExit += terminateChild;
        try
        {
            process.WaitForExit();
            return process.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= waitForChild;
            AppDomain.CurrentDomain.ProcessExit -= terminateChild;
        }
    }

    private static string? ResolveTarget()
    {
        string? architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (architecture is null)
            return null;
        if (OperatingSystem.IsWindows())
            return $"win-{architecture}";
        if (OperatingSystem.IsLinux() && !RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal))
            return $"linux-{architecture}";

        return null;
    }

    private static void StopChild(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The application can exit concurrently with launcher shutdown.
        }
    }
}
