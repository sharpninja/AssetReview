using System.Diagnostics;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using static Nuke.Common.Tooling.ProcessTasks;

class Build : NukeBuild
{
    private const string AssetReviewToolPackageId = "SharpNinja.AssetReview.Tool";
    private const string AssetReviewToolCommandName = "asset-review";
    private const string DefaultNuGetSource = "https://api.nuget.org/v3/index.json";

    private GitVersionInfo? _gitVersion;

    [Parameter("Build configuration.")]
    readonly string Configuration = "Release";

    [Parameter("NuGet push source URL. Default: nuget.org v3 index.")]
    readonly string NuGetSource = DefaultNuGetSource;

    AbsolutePath SolutionFile => RootDirectory / "AssetReview.sln";
    AbsolutePath AssetReviewToolProject => RootDirectory / "src" / "AssetReview.Tool" / "AssetReview.Tool.csproj";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts" / "nuke";
    AbsolutePath LocalToolPackagesDirectory => ArtifactsDirectory / "local-packages";
    AbsolutePath LocalToolNuGetConfigFile => LocalToolPackagesDirectory / "nuget.config";
    AbsolutePath DotNetGlobalToolsDirectory => (AbsolutePath)Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) / ".dotnet" / "tools";
    AbsolutePath AssetReviewToolExecutablePath => DotNetGlobalToolsDirectory / (OperatingSystem.IsWindows() ? $"{AssetReviewToolCommandName}.exe" : AssetReviewToolCommandName);
    AbsolutePath VersionJsonPath => ArtifactsDirectory / "version.json";

    /// <summary>
    /// NuGet package version from GitVersion <c>SemVer</c>.
    /// NuGet rejects build metadata, so a trailing <c>+…</c> is removed.
    /// </summary>
    string ResolvedPackageVersion => ToNuGetVersion(GitVersion().SemVer);

    public static int Main(string[] args)
    {
        if (args.Any(IsHelpArgument))
        {
            PrintHelp();
            return 0;
        }

        return Execute<Build>(x => x.Compile);
    }

    Target Clean => _ => _
        .Executes(() =>
        {
            CleanDirectory(ArtifactsDirectory);
        });

    /// <summary>Resolve GitVersion once and write version.json for packages/assemblies.</summary>
    Target CalculateVersion => _ => _
        .Executes(() =>
        {
            var gv = GitVersion();
            EnsureDirectory(ArtifactsDirectory);
            WriteVersionJson(gv);
            Console.WriteLine($"Version artifacts synced to GitVersion SemVer={gv.SemVer} MMP={gv.MajorMinorPatch}");
        });

    Target Restore => _ => _
        .DependsOn(CalculateVersion)
        .Executes(() =>
        {
            DotNet("tool restore");
            DotNet($"restore \"{SolutionFile}\"");
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNet($"build \"{SolutionFile}\" --configuration {Configuration} --no-restore --verbosity minimal {VersionMsBuildArgs()}");
        });

    Target PackAssetReviewTool => _ => _
        .DependsOn(CalculateVersion)
        .Executes(() =>
        {
            CleanDirectory(LocalToolPackagesDirectory);
            DotNet($"restore \"{AssetReviewToolProject}\"");
            DotNet(
                $"pack \"{AssetReviewToolProject}\" --configuration {Configuration} --no-restore " +
                $"--output \"{LocalToolPackagesDirectory}\" {VersionMsBuildArgs()}");
            var packagePath = AssetReviewToolPackagePath();
            if (!File.Exists(packagePath))
            {
                var found = Directory.Exists(LocalToolPackagesDirectory)
                    ? string.Join(", ", Directory.GetFiles(LocalToolPackagesDirectory, "*.nupkg"))
                    : "(directory missing)";
                throw new FileNotFoundException(
                    $"Pack did not produce the GitVersion package '{packagePath}'. Found: {found}",
                    packagePath);
            }

            WriteLocalToolNuGetConfig();
            Console.WriteLine($"AssetReview tool package: {packagePath} (GitVersion SemVer {ResolvedPackageVersion})");
        });

    Target DeployAssetReviewTool => _ => _
        .DependsOn(PackAssetReviewTool)
        .Executes(() =>
        {
            var version = AssetReviewToolVersion();
            if (IsGlobalDotNetToolInstalled(AssetReviewToolPackageId))
            {
                DotNet($"tool uninstall --global {AssetReviewToolPackageId}");
            }
            else
            {
                Console.WriteLine($"{AssetReviewToolPackageId} is not currently installed as a global tool.");
            }

            DotNet($"tool install --global {AssetReviewToolPackageId} --version {version} --configfile \"{LocalToolNuGetConfigFile}\" --ignore-failed-sources");
            if (!File.Exists(AssetReviewToolExecutablePath))
            {
                throw new FileNotFoundException("AssetReview tool install did not produce the expected executable.", AssetReviewToolExecutablePath);
            }

            Run(AssetReviewToolExecutablePath, "--version");
        });

    /// <summary>
    /// Push the packed nupkg to nuget.org (or --nuget-source).
    /// Requires NUGET_API_KEY (preferred) or NUGET_AUTH_TOKEN in the environment.
    /// </summary>
    Target PublishToNuGet => _ => _
        .DependsOn(PackAssetReviewTool)
        .Executes(() =>
        {
            var apiKey = ResolveNuGetApiKey();
            var packagePath = AssetReviewToolPackagePath();
            if (!File.Exists(packagePath))
            {
                throw new FileNotFoundException("Packed nupkg not found. Run PackAssetReviewTool first.", packagePath);
            }

            Console.WriteLine($"Pushing {packagePath} to {NuGetSource} (API key from environment, not printed).");
            // Avoid Nuke ProcessTasks logging the raw API key on the command line.
            PushNuGetPackage(packagePath, apiKey, NuGetSource);
        });

    AbsolutePath AssetReviewToolPackagePath()
    {
        return LocalToolPackagesDirectory / $"{AssetReviewToolPackageId}.{ResolvedPackageVersion}.nupkg";
    }

    string AssetReviewToolVersion() => ResolvedPackageVersion;

    void PushNuGetPackage(AbsolutePath packagePath, string apiKey, string source)
    {
        var arguments = $"nuget push \"{packagePath}\" --api-key {apiKey} --source \"{source}\" --skip-duplicate";
        Console.WriteLine($"dotnet nuget push \"{packagePath}\" --api-key *** --source \"{source}\" --skip-duplicate");
        var startInfo = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet nuget push.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(stdout))
            Console.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(stderr.TrimEnd());
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet nuget push failed with exit code {process.ExitCode}.");
        }
    }

    static string ResolveNuGetApiKey()
    {
        foreach (var name in new[] { "NUGET_API_KEY", "NUGET_AUTH_TOKEN" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        throw new InvalidOperationException(
            "Missing NuGet API key. Set environment variable NUGET_API_KEY (preferred) or NUGET_AUTH_TOKEN before PublishToNuGet.");
    }

    void WriteLocalToolNuGetConfig()
    {
        EnsureDirectory(LocalToolPackagesDirectory);
        var document = new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add",
                        new XAttribute("key", "local-tool-packages"),
                        new XAttribute("value", LocalToolPackagesDirectory.ToString())))));
        document.Save(LocalToolNuGetConfigFile);
    }

    GitVersionInfo GitVersion()
    {
        if (_gitVersion is not null)
            return _gitVersion;

        // CalculateVersion runs before Restore, so the local tool must be restored here.
        DotNet("tool restore");

        string json;
        try
        {
            json = RunAndCapture("dotnet", "tool run dotnet-gitversion -- /output json /verbosity quiet");
        }
        catch (InvalidOperationException)
        {
            json = RunAndCapture("dotnet-gitversion", "/output json /verbosity quiet");
        }

        _gitVersion = GitVersionInfo.Parse(ExtractJson(json));
        Console.WriteLine(
            $"GitVersion: SemVer={_gitVersion.SemVer} MajorMinorPatch={_gitVersion.MajorMinorPatch} " +
            $"Assembly={_gitVersion.AssemblySemVer} Informational={_gitVersion.InformationalVersion}");
        return _gitVersion;
    }

    string VersionMsBuildArgs()
    {
        var gv = GitVersion();
        var packageVersion = ToNuGetVersion(gv.SemVer);
        return
            $"/p:Version={packageVersion} " +
            $"/p:PackageVersion={packageVersion} " +
            $"/p:AssemblyVersion={gv.AssemblySemVer} " +
            $"/p:FileVersion={gv.AssemblySemFileVer} " +
            $"/p:InformationalVersion=\"{gv.InformationalVersion}\" " +
            $"/p:ProductVersion={gv.MajorMinorPatch} " +
            "/p:IncludeSourceRevisionInInformationalVersion=false";
    }

    void WriteVersionJson(GitVersionInfo gv)
    {
        var payload = new Dictionary<string, string?>
        {
            ["SemVer"] = gv.SemVer,
            ["MajorMinorPatch"] = gv.MajorMinorPatch,
            ["AssemblySemVer"] = gv.AssemblySemVer,
            ["AssemblySemFileVer"] = gv.AssemblySemFileVer,
            ["InformationalVersion"] = gv.InformationalVersion,
            ["PackageVersion"] = ResolvedPackageVersion,
            ["Sha"] = gv.Sha,
            ["BranchName"] = gv.BranchName,
        };
        File.WriteAllText(
            VersionJsonPath,
            System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wrote {VersionJsonPath}");
    }

    bool IsGlobalDotNetToolInstalled(string packageId)
    {
        var output = RunAndCapture("dotnet", "tool list --global");
        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(2)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Any(id => string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase));
    }

    string RunAndCapture(string tool, string arguments)
    {
        var startInfo = new ProcessStartInfo(tool, arguments)
        {
            WorkingDirectory = RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {tool}.");
        // Read both streams before waiting so a chatty GitVersion stderr cannot deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var standardOutput = stdoutTask.GetAwaiter().GetResult();
        var standardError = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} {arguments} failed with exit code {process.ExitCode}: {standardError}");
        }

        return standardOutput;
    }

    static void CleanDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    void DotNet(string arguments)
    {
        Run("dotnet", arguments);
    }

    void Run(string tool, string arguments)
    {
        StartProcess(tool, arguments, RootDirectory).AssertZeroExitCode();
    }

    static string ToNuGetVersion(string semVer)
    {
        var plus = semVer.IndexOf('+', StringComparison.Ordinal);
        var version = plus >= 0 ? semVer[..plus] : semVer;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"GitVersion SemVer '{semVer}' is not a NuGet package version.");
        }

        return version;
    }

    static string ExtractJson(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException($"GitVersion did not return JSON. Output: {output}");
        }

        return output[start..(end + 1)];
    }

    static bool IsHelpArgument(string argument)
    {
        return argument is "--help" or "-h" or "/?" or "help";
    }

    static void PrintHelp()
    {
        Console.WriteLine("AssetReview build");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  .\\build.ps1 [target] [--parameter value]");
        Console.WriteLine();
        Console.WriteLine("Targets:");
        Console.WriteLine("  Clean");
        Console.WriteLine("  CalculateVersion             # GitVersion -> artifacts/nuke/version.json");
        Console.WriteLine("  Restore");
        Console.WriteLine("  Compile                      # default; assemblies use GitVersion");
        Console.WriteLine("  PackAssetReviewTool          # nupkg version = SemVer");
        Console.WriteLine("  DeployAssetReviewTool        # uninstall-if-present + install --global from local feed");
        Console.WriteLine("  PublishToNuGet               # dotnet nuget push (requires NUGET_API_KEY)");
        Console.WriteLine();
        Console.WriteLine("Parameters:");
        Console.WriteLine("  --configuration <Debug|Release>    Build configuration. Default: Release");
        Console.WriteLine("  --nuget-source <url>               Push feed. Default: https://api.nuget.org/v3/index.json");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  NUGET_API_KEY                      Preferred API key for PublishToNuGet");
        Console.WriteLine("  NUGET_AUTH_TOKEN                   Fallback API key if NUGET_API_KEY is unset");
        Console.WriteLine();
        Console.WriteLine("Versioning: packages and assemblies share GitVersion.yml");
        Console.WriteLine("  (ContinuousDeployment, next-version 0.1.0).");
        Console.WriteLine("  Run:  dotnet tool restore && dotnet tool run dotnet-gitversion");
    }
}

/// <summary>Subset of GitVersion JSON used by Nuke packaging and MSBuild version props.</summary>
sealed class GitVersionInfo
{
    public required string SemVer { get; init; }
    public required string MajorMinorPatch { get; init; }
    public required string AssemblySemVer { get; init; }
    public required string AssemblySemFileVer { get; init; }
    public required string InformationalVersion { get; init; }
    public required string Sha { get; init; }
    public required string BranchName { get; init; }

    public static GitVersionInfo Parse(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Req(string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                ? p.GetString()!
                : throw new InvalidOperationException($"GitVersion JSON missing string property '{name}'.");

        return new GitVersionInfo
        {
            SemVer = Req("SemVer"),
            MajorMinorPatch = Req("MajorMinorPatch"),
            AssemblySemVer = Req("AssemblySemVer"),
            AssemblySemFileVer = Req("AssemblySemFileVer"),
            InformationalVersion = Req("InformationalVersion"),
            Sha = Req("Sha"),
            BranchName = Req("BranchName"),
        };
    }
}