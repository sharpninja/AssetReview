using System.Diagnostics;
using SharpNinja.AssetReview.Tool;

namespace AssetReview.AiUnit.Tests;

internal readonly record struct ToolResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ToolProcess
{
    public static string ToolDll => typeof(ReviewLaunchMode).Assembly.Location;

    public static ProcessStartInfo Create(IReadOnlyList<string> appArgs, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(FindDotNet())
        {
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(ToolDll);
        foreach (var arg in appArgs)
            start.ArgumentList.Add(arg);
        return start;
    }

    public static async Task<ToolResult> RunAsync(
        IReadOnlyList<string> appArgs,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        using var process = new Process { StartInfo = Create(appArgs, workingDirectory) };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start asset-review.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ToolResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindDotNet()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
                return candidate;
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}
