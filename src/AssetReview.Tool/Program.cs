using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BBCrawler.Tooling.Tests")]

namespace SharpNinja.AssetReview.Tool;

internal static class Program
{
    internal static string VersionText =>
        "asset-review "
        + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(arg => arg is "--version" or "-v"))
        {
            Console.WriteLine(VersionText);
            return 0;
        }

        var options = ReviewOptions.Parse(args);
        EnsureParentDirectory(options.FeedbackFile);

        var app = CreateReviewServer(options);
        app.StartAsync().GetAwaiter().GetResult();

        string url = options.ReviewUrl;
        WriteStartup(options, url);

        try
        {
            switch (options.LaunchMode)
            {
                case ReviewLaunchMode.Embedded:
                    return RunEmbeddedHost(options, url);
                case ReviewLaunchMode.ExternalBrowser:
                    TryOpenBrowser(url);
                    app.WaitForShutdownAsync().GetAwaiter().GetResult();
                    return 0;
                case ReviewLaunchMode.ServerOnly:
                    app.WaitForShutdownAsync().GetAwaiter().GetResult();
                    return 0;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options.LaunchMode, "Unsupported review launch mode.");
            }
        }
        finally
        {
            StopReviewServer(app);
        }
    }

    internal static WebApplication CreateReviewServer(ReviewOptions options)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");

        var app = builder.Build();

        app.MapGet("/", () => Results.Content(Html, "text/html; charset=utf-8"));
        app.MapGet("/api/config", () => Results.Json(new
        {
            title = options.Title,
            workspace = options.Workspace,
            assetRoot = options.AssetRoot,
            feedbackFile = options.FeedbackFile,
            feedbackAssetRoot = options.FeedbackAssetRoot,
            manifestFiles = options.ManifestFiles,
        }));

        app.MapGet("/api/assets", () => Results.Json(BuildIndex(options)));
        app.MapGet("/api/animation-sets", () => Results.Json(BuildAnimationSets(options)));
        app.MapPost("/api/feedback", async (FeedbackRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.AssetPath))
                return Results.BadRequest(new { error = "assetPath is required" });
            if (request.Decision is not ("approved" or "refinement"))
                return Results.BadRequest(new { error = "decision must be approved or refinement" });

            string normalized = NormalizeRelativePath(request.AssetPath);
            string absolute = Path.GetFullPath(Path.Combine(options.AssetRoot, normalized));
            if (!IsInside(options.AssetRoot, absolute) || !File.Exists(absolute))
                return Results.NotFound(new { error = "asset not found" });

            string feedbackAssetPath = ToFeedbackAssetPath(options, absolute, normalized);
            var entry = new FeedbackEntry(
                DateTimeOffset.UtcNow,
                feedbackAssetPath,
                request.Decision,
                request.Comment?.Trim() ?? string.Empty,
                request.Context?.Trim() ?? string.Empty);

            await using var stream = new FileStream(options.FeedbackFile, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions));
            return Results.Json(entry);
        });

        app.MapPost("/api/feedback/clear-non-approvals", async () =>
        {
            int keptApprovals = await ClearFeedbackExceptApprovals(options);
            return Results.Json(new { keptApprovals });
        });

        app.MapGet("/assets/{**path}", (string path) =>
        {
            string normalized = NormalizeRelativePath(path);
            string absolute = Path.GetFullPath(Path.Combine(options.AssetRoot, normalized));
            if (!IsInside(options.AssetRoot, absolute) || !File.Exists(absolute))
                return Results.NotFound();

            string contentType = Path.GetExtension(absolute).Equals(".svg", StringComparison.OrdinalIgnoreCase)
                ? "image/svg+xml"
                : "image/png";
            return Results.File(absolute, contentType, enableRangeProcessing: true);
        });

        return app;
    }

    private static int RunEmbeddedHost(ReviewOptions options, string url)
    {
        AssetReviewDesktopHost.Context = new AssetReviewHostContext(options, new Uri(url, UriKind.Absolute));
        return AppBuilder.Configure<AssetReviewApp>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
    }

    private static void WriteStartup(ReviewOptions options, string url)
    {
        Console.WriteLine($"Asset Review running at {url}");
        Console.WriteLine($"Workspace: {options.Workspace}");
        Console.WriteLine($"Asset root: {options.AssetRoot}");
        Console.WriteLine($"Feedback file: {options.FeedbackFile}");
        Console.WriteLine($"Feedback asset root: {options.FeedbackAssetRoot}");
        if (options.ManifestFiles.Length > 0)
            Console.WriteLine($"Manifest files: {string.Join(", ", options.ManifestFiles)}");
        Console.WriteLine($"Launch mode: {options.LaunchMode}");
    }

    internal static void StopReviewServer(WebApplication app)
    {
        Task.Run(async () =>
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    internal static AssetIndex BuildIndex(ReviewOptions options)
    {
        string[] manifestFiles = SelectManifestFilesForIndex(options);
        var feedback = ReadLatestFeedback(options);
        var assetPaths = EnumerateManifestAssets(options, manifestFiles).ToArray();
        if (assetPaths.Length == 0)
            assetPaths = EnumerateAssets(options).ToArray();

        var assets = assetPaths.Select(path =>
        {
            string relative = Path.GetRelativePath(options.AssetRoot, path).Replace('\\', '/');
            string folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
            var file = new FileInfo(path);
            feedback.TryGetValue(relative, out var status);
            return new AssetRecord(
                relative,
                folder.Length == 0 ? "." : folder,
                Path.GetFileName(relative),
                Path.GetExtension(relative).TrimStart('.').ToLowerInvariant(),
                file.Length,
                status?.Decision,
                status?.Comment,
                status?.TimestampUtc);
        }).OrderBy(a => a.Folder, StringComparer.OrdinalIgnoreCase)
          .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
          .ToArray();

        var folders = assets.GroupBy(a => a.Folder)
            .Select(g => new FolderRecord(
                g.Key,
                g.Count(),
                g.Count(a => a.Decision == "approved"),
                g.Count(a => a.Decision == "refinement")))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AssetIndex(options.Workspace, options.AssetRoot, options.FeedbackFile, options.FeedbackAssetRoot, manifestFiles, assets, folders);
    }

    internal static IReadOnlyList<CharacterAnimationSet> BuildAnimationSets(ReviewOptions options)
    {
        AssetIndex index = BuildIndex(options);
        return CombatAnimationSets.Build(index.Assets.Select(asset => asset.Path));
    }

    internal static string[] SelectManifestFilesForIndex(ReviewOptions options)
    {
        string[] manifestFiles = options.ManifestFiles
            .Where(File.Exists)
            .OrderBy(ManifestPriority)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] feedbackRootManifests = manifestFiles
            .Where(path =>
            {
                string? manifestRoot = Path.GetDirectoryName(path);
                return !string.IsNullOrWhiteSpace(manifestRoot) &&
                    IsInside(options.FeedbackAssetRoot, manifestRoot);
            })
            .ToArray();

        return feedbackRootManifests.Length > 0 ? feedbackRootManifests : manifestFiles;
    }

    private static IEnumerable<string> EnumerateAssets(ReviewOptions options)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".vscode", ".idea", "bin", "obj", "node_modules", "packages", ".nuget",
            "cache", ".mcpServer", ".asset-review", "debug", "test-output", "screenshots", "crawl-shots",
        };

        var stack = new Stack<string>();
        stack.Push(options.AssetRoot);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                if (IsReviewAssetCandidate(options, file))
                {
                    yield return file;
                }
            }

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (string child in dirs)
            {
                if (!excluded.Contains(Path.GetFileName(child)) && !IsExcludedDebugAssetPath(options, child))
                    stack.Push(child);
            }
        }
    }

    private static IEnumerable<string> EnumerateManifestAssets(ReviewOptions options, IReadOnlyList<string> manifestFiles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifestFile in manifestFiles)
        {
            string? manifestRoot = Path.GetDirectoryName(manifestFile);
            if (string.IsNullOrWhiteSpace(manifestRoot))
                continue;

            IEnumerable<string> lines;
            try
            {
                lines = File.ReadLines(manifestFile);
            }
            catch
            {
                continue;
            }

            foreach (string line in lines)
            foreach (string assetPath in ExtractManifestAssetPaths(line))
            {
                string absolute = Path.GetFullPath(Path.Combine(manifestRoot, NormalizeRelativePath(assetPath)));
                if (!IsInside(options.AssetRoot, absolute) ||
                    !File.Exists(absolute) ||
                    !IsReviewAssetCandidate(options, absolute) ||
                    !seen.Add(absolute))
                {
                    continue;
                }

                yield return absolute;
            }
        }
    }

    private static IEnumerable<string> ExtractManifestAssetPaths(string line)
    {
        int start = 0;
        while (start < line.Length)
        {
            int open = line.IndexOf('`', start);
            if (open < 0)
                yield break;

            int close = line.IndexOf('`', open + 1);
            if (close < 0)
                yield break;

            string value = line[(open + 1)..close].Trim();
            string ext = Path.GetExtension(value);
            if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                yield return value;
            }

            start = close + 1;
        }
    }

    internal static bool IsReviewAssetCandidate(ReviewOptions options, string path)
    {
        string ext = Path.GetExtension(path);
        if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsExcludedDebugAssetPath(options, path);
    }

    private static bool IsExcludedDebugAssetPath(ReviewOptions options, string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.Equals("shot-adj.png", StringComparison.OrdinalIgnoreCase))
            return true;

        string relative;
        try
        {
            relative = Path.GetRelativePath(options.Workspace, path);
        }
        catch
        {
            return false;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;

        string normalized = relative.Replace('\\', '/');
        if (normalized.Equals("BBCrawler/splash", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("BBCrawler/splash/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Equals("artifacts/art-approval", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("artifacts/art-approval/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("debug", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("test-output", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("screenshots", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("crawl-shots", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, FeedbackEntry> ReadLatestFeedback(ReviewOptions options)
    {
        var latest = new Dictionary<string, FeedbackEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(options.FeedbackFile))
            return latest;

        foreach (string line in File.ReadLines(options.FeedbackFile))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var entry = JsonSerializer.Deserialize<FeedbackEntry>(line, JsonOptions);
                if (entry is null)
                    continue;

                string normalized = NormalizeRelativePath(entry.AssetPath);
                latest[normalized] = entry;

                string absolute = Path.GetFullPath(Path.Combine(options.FeedbackAssetRoot, normalized));
                if (IsInside(options.AssetRoot, absolute))
                    latest[Path.GetRelativePath(options.AssetRoot, absolute).Replace('\\', '/')] = entry;
            }
            catch
            {
                // Keep review usable if a feedback file was hand-edited.
            }
        }

        return latest;
    }

    private static async Task<int> ClearFeedbackExceptApprovals(ReviewOptions options)
    {
        EnsureParentDirectory(options.FeedbackFile);
        var approvals = ReadLatestFeedback(options)
            .Values
            .DistinctBy(entry => entry.AssetPath)
            .Where(entry => entry.Decision == "approved")
            .OrderBy(entry => entry.AssetPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string tempFile = options.FeedbackFile + ".tmp";
        await using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.Read))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (FeedbackEntry entry in approvals)
                await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions));
        }

        File.Move(tempFile, options.FeedbackFile, overwrite: true);
        return approvals.Length;
    }

    private static string ToFeedbackAssetPath(ReviewOptions options, string absoluteAssetPath, string fallbackAssetPath)
    {
        if (IsInside(options.FeedbackAssetRoot, absoluteAssetPath))
            return Path.GetRelativePath(options.FeedbackAssetRoot, absoluteAssetPath).Replace('\\', '/');

        return fallbackAssetPath;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    internal static bool IsInside(string root, string candidate)
    {
        // Canonicalize both paths (collapsing "..") and reject anything that escapes the root.
        // Path.GetRelativePath is platform-correct, unlike the previous OrdinalIgnoreCase prefix
        // match which both failed on case-sensitive filesystems and let "..\" traversals through
        // (TR-CRAWLER-TOOL-019).
        string fullRoot = Path.GetFullPath(root);
        string fullCandidate = Path.GetFullPath(candidate);
        string relative = Path.GetRelativePath(fullRoot, fullCandidate);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    internal static bool TryParsePort(string? raw, out int port, out string error)
    {
        if (!int.TryParse(raw, out port))
        {
            port = 0;
            error = $"Port '{raw}' is not a valid integer.";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = $"Port {port} is out of range (1-65535).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void EnsureParentDirectory(string file)
    {
        string? directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    internal static string[] DiscoverManifestFiles(string workspace, string assetRoot) =>
        EnumerateFilesByName(assetRoot, "_manifest.md")
            .Where(path => IsInside(workspace, path))
            .OrderBy(ManifestPriority)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static string DiscoverFeedbackFile(string workspace, string assetRoot, IReadOnlyList<string> manifestFiles)
    {
        var candidates = new List<string>
        {
            Path.Combine(workspace, "docs", "Project", "Art-Approval-Feedback.jsonl"),
            Path.Combine(workspace, "Art-Approval-Feedback.jsonl"),
            Path.Combine(assetRoot, "Art-Approval-Feedback.jsonl"),
        };

        foreach (string manifestFile in manifestFiles)
        {
            string? current = Path.GetDirectoryName(manifestFile);
            while (!string.IsNullOrWhiteSpace(current) && IsInside(workspace, current))
            {
                candidates.Add(Path.Combine(current, "Art-Approval-Feedback.jsonl"));
                string? parent = Directory.GetParent(current)?.FullName;
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string fullPath = Path.GetFullPath(candidate);
            if (IsInside(workspace, fullPath) && File.Exists(fullPath))
                return fullPath;
        }

        return Path.Combine(workspace, ".asset-review", "feedback.jsonl");
    }

    internal static string ResolveFeedbackAssetRoot(
        string workspace,
        string assetRoot,
        string feedbackFile,
        IReadOnlyList<string> manifestFiles)
    {
        string feedbackDirectory = Path.GetDirectoryName(feedbackFile) ?? workspace;
        string originalArtRoot = Path.Combine(feedbackDirectory, "Art-Source", "full-refresh");
        if (Directory.Exists(originalArtRoot))
            return Path.GetFullPath(originalArtRoot);

        foreach (string manifestFile in manifestFiles.OrderBy(ManifestPriority))
        {
            string? manifestRoot = Path.GetDirectoryName(manifestFile);
            if (!string.IsNullOrWhiteSpace(manifestRoot) && IsInside(feedbackDirectory, manifestRoot))
                return Path.GetFullPath(manifestRoot);
        }

        return assetRoot;
    }

    private static IEnumerable<string> EnumerateFilesByName(string root, string fileName)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".vscode", ".idea", "bin", "obj", "node_modules", "packages", ".nuget",
            "cache", ".mcpServer", ".asset-review", "debug", "test-output", "screenshots", "crawl-shots",
        };

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, fileName);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
                yield return Path.GetFullPath(file);

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (string child in dirs)
            {
                if (!excluded.Contains(Path.GetFileName(child)))
                    stack.Push(child);
            }
        }
    }

    private static int ManifestPriority(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.Contains("/docs/Project/Art-Source/", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (normalized.Contains("/artifacts/art-approval/", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            Console.WriteLine("Could not open browser automatically.");
        }
    }

    internal const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Asset Review</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f6f8fa;
      --surface: #ffffff;
      --surface-2: #f6f8fa;
      --text: #1f2328;
      --muted: #656d76;
      --border: #d0d7de;
      --accent: #0969da;
      --approve: #1a7f37;
      --refine: #9a6700;
      --danger: #cf222e;
      --toolbar-bg: rgba(246, 248, 250, .88);
      --shadow: 0 8px 24px rgba(140, 149, 159, .20);
      --asset-bg: #000000;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        color-scheme: dark;
        --bg: #0d1117;
        --surface: #161b22;
        --surface-2: #21262d;
        --text: #e6edf3;
        --muted: #7d8590;
        --border: #30363d;
        --accent: #2f81f7;
        --approve: #3fb950;
        --refine: #d29922;
        --danger: #f85149;
        --toolbar-bg: rgba(13, 17, 23, .88);
        --shadow: 0 16px 32px rgba(1, 4, 9, .45);
      }
    }
    body[data-theme="light"] {
      color-scheme: light;
      --bg: #f6f8fa;
      --surface: #ffffff;
      --surface-2: #f6f8fa;
      --text: #1f2328;
      --muted: #656d76;
      --border: #d0d7de;
      --accent: #0969da;
      --approve: #1a7f37;
      --refine: #9a6700;
      --danger: #cf222e;
      --toolbar-bg: rgba(246, 248, 250, .88);
      --shadow: 0 8px 24px rgba(140, 149, 159, .20);
    }
    body[data-theme="dark"] {
      color-scheme: dark;
      --bg: #0d1117;
      --surface: #161b22;
      --surface-2: #21262d;
      --text: #e6edf3;
      --muted: #7d8590;
      --border: #30363d;
      --accent: #2f81f7;
      --approve: #3fb950;
      --refine: #d29922;
      --danger: #f85149;
      --toolbar-bg: rgba(13, 17, 23, .88);
      --shadow: 0 16px 32px rgba(1, 4, 9, .45);
    }
    * { box-sizing: border-box; }
    body { margin: 0; background: var(--bg); color: var(--text); }
    button, input, textarea, select { font: inherit; }
    button { color: inherit; }
    .app { display: grid; grid-template-columns: 284px minmax(0, 1fr); min-height: 100vh; }
    .rail { border-right: 1px solid var(--border); background: var(--surface); padding: 18px 14px; display: flex; flex-direction: column; gap: 16px; min-width: 0; }
    .brand { display: flex; align-items: center; gap: 10px; font-weight: 700; }
    .brand-mark { width: 30px; height: 30px; display: grid; place-items: center; color: #ffffff; background: var(--text); border-radius: 6px; }
    .brand-mark svg { width: 18px; height: 18px; stroke-width: 2; }
    .meta { color: var(--muted); font-size: 12px; line-height: 1.45; word-break: break-word; }
    .search { width: 100%; border: 1px solid var(--border); border-radius: 7px; padding: 9px 10px; background: var(--surface); color: var(--text); }
    .folder-list { overflow: auto; display: flex; flex-direction: column; gap: 4px; padding-right: 2px; }
    .folder { width: 100%; border: 0; background: transparent; text-align: left; padding: 8px 9px; border-radius: 7px; display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 8px; cursor: pointer; }
    .folder:hover, .folder.active { background: var(--surface-2); }
    .folder span:first-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .badge { font-size: 11px; color: var(--muted); }
    .main { display: grid; grid-template-rows: auto minmax(0, 1fr); min-width: 0; }
    .toolbar { height: 62px; border-bottom: 1px solid var(--border); background: var(--toolbar-bg); backdrop-filter: blur(10px); display: flex; align-items: center; justify-content: space-between; padding: 0 22px; gap: 12px; }
    .toolbar h1 { font-size: 17px; margin: 0; font-weight: 700; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .toolbar-actions { display: flex; align-items: center; gap: 8px; }
    .reload-button { min-height: 36px; border: 1px solid var(--border); border-radius: 7px; background: var(--surface); display: inline-flex; align-items: center; gap: 7px; padding: 0 10px; color: var(--text); cursor: pointer; }
    .reload-button:hover { background: var(--surface-2); }
    .reload-button:disabled { cursor: wait; opacity: .65; }
    .reload-button svg { width: 17px; height: 17px; stroke-width: 2; }
    .clear-button { min-height: 36px; border: 1px solid color-mix(in srgb, var(--refine) 35%, transparent); border-radius: 7px; background: color-mix(in srgb, var(--refine) 8%, transparent); display: inline-flex; align-items: center; gap: 7px; padding: 0 10px; color: var(--refine); cursor: pointer; }
    .clear-button:hover { background: color-mix(in srgb, var(--refine) 14%, transparent); }
    .clear-button:disabled { cursor: wait; opacity: .65; }
    .clear-button svg { width: 17px; height: 17px; stroke-width: 2; }
    .segmented { display: inline-flex; border: 1px solid var(--border); border-radius: 7px; overflow: hidden; background: var(--surface); }
    .segmented button { border: 0; background: transparent; padding: 8px 11px; color: var(--muted); cursor: pointer; }
    .segmented button.active { background: var(--accent); color: #ffffff; }
    .content { min-width: 0; overflow: hidden; }
    .grid-view { height: calc(100vh - 62px); overflow: auto; padding: 22px; }
    .asset-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(148px, 1fr)); gap: 14px; }
    .asset-card { border: 1px solid var(--border); border-radius: 8px; background: var(--surface); overflow: hidden; cursor: pointer; transition: transform .12s ease, box-shadow .12s ease, border-color .12s ease; padding: 0; text-align: left; }
    .asset-card:hover { transform: translateY(-1px); box-shadow: var(--shadow); border-color: var(--accent); }
    .thumb { height: 118px; display: grid; place-items: center; background: var(--asset-bg); }
    .thumb img { max-width: 92%; max-height: 92%; image-rendering: pixelated; object-fit: contain; }
    .asset-info { padding: 10px; display: grid; gap: 6px; }
    .asset-name { font-size: 13px; font-weight: 650; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .asset-folder { font-size: 11px; color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .status { font-size: 11px; width: fit-content; padding: 3px 7px; border-radius: 999px; border: 1px solid var(--border); color: var(--muted); text-transform: capitalize; }
    .status.approved { color: var(--approve); border-color: color-mix(in srgb, var(--approve) 35%, transparent); background: color-mix(in srgb, var(--approve) 8%, transparent); }
    .status.refinement { color: var(--refine); border-color: color-mix(in srgb, var(--refine) 35%, transparent); background: color-mix(in srgb, var(--refine) 10%, transparent); }
    .detail { height: calc(100vh - 62px); display: grid; grid-template-columns: minmax(0, 1fr) 360px; min-width: 0; }
    .stage-wrap { min-width: 0; padding: 22px; display: grid; grid-template-rows: auto minmax(0, 1fr); gap: 14px; }
    .detail-nav { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-width: 0; }
    .icon-row { display: flex; gap: 8px; align-items: center; }
    .icon-button { width: 38px; height: 38px; border: 1px solid var(--border); border-radius: 7px; background: var(--surface); display: grid; place-items: center; cursor: pointer; }
    .icon-button:hover { background: var(--surface-2); }
    .icon-button svg { width: 18px; height: 18px; stroke-width: 2; }
    .detail-controls { display: flex; align-items: center; justify-content: flex-end; gap: 14px; min-width: 0; flex-wrap: wrap; }
    .zoom-control { display: grid; grid-template-columns: auto 118px 42px; gap: 8px; align-items: center; color: var(--muted); font-size: 12px; }
    .zoom-control input { accent-color: var(--accent); }
    .palette { display: grid; grid-template-columns: repeat(8, 22px); gap: 5px; align-items: center; }
    .swatch { width: 22px; height: 22px; border: 1px solid rgba(0,0,0,.28); border-radius: 5px; cursor: pointer; box-shadow: inset 0 0 0 1px rgba(255,255,255,.24); }
    .swatch.active { outline: 2px solid var(--text); outline-offset: 2px; }
    .stage { min-height: 0; border: 1px solid var(--border); border-radius: 8px; background: var(--surface); box-shadow: var(--shadow); display: grid; grid-template-rows: auto minmax(0, 1fr); overflow: hidden; }
    .context-bar { min-height: 42px; border-bottom: 1px solid var(--border); display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 0 12px; color: var(--muted); font-size: 12px; }
    .context-bar span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .preview { min-height: 0; display: grid; place-items: safe center; padding: 28px; background: var(--asset-bg); overflow: auto; }
    .preview img { max-width: none; max-height: none; object-fit: contain; image-rendering: pixelated; }
    .anim-player { border-top: 1px solid var(--border); padding: 12px; display: grid; gap: 10px; background: var(--surface-2); }
    .anim-player.hidden { display: none; }
    .anim-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .anim-row label { font-size: 12px; color: var(--muted); display: inline-flex; align-items: center; gap: 6px; }
    .anim-seq-buttons { display: flex; flex-wrap: wrap; gap: 6px; }
    .anim-seq-buttons button { border: 1px solid var(--border); border-radius: 999px; background: var(--surface); color: var(--text); padding: 5px 10px; font-size: 12px; cursor: pointer; }
    .anim-seq-buttons button.active { background: var(--accent); border-color: var(--accent); color: #fff; }
    .anim-play-btn { min-height: 34px; border: 1px solid var(--border); border-radius: 7px; background: var(--surface); padding: 0 12px; cursor: pointer; font-weight: 650; }
    .anim-play-btn.playing { background: color-mix(in srgb, var(--approve) 14%, transparent); border-color: color-mix(in srgb, var(--approve) 40%, transparent); color: var(--approve); }
    .anim-meta { font-size: 12px; color: var(--muted); }
    .review-panel { border-left: 1px solid var(--border); background: var(--surface); padding: 20px; display: flex; flex-direction: column; gap: 14px; min-width: 0; }
    .review-panel h2 { margin: 0; font-size: 16px; line-height: 1.25; word-break: break-word; }
    .detail-path { color: var(--muted); font-size: 12px; word-break: break-word; }
    textarea { width: 100%; min-height: 180px; resize: vertical; border: 1px solid var(--border); border-radius: 7px; padding: 10px; line-height: 1.45; }
    .primary-actions { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    .action { border: 1px solid var(--border); border-radius: 7px; padding: 10px 12px; font-weight: 700; cursor: pointer; min-height: 42px; }
    .approve { color: white; background: var(--approve); border-color: var(--approve); }
    .refine { color: white; background: var(--refine); border-color: var(--refine); }
    .toast { min-height: 20px; color: var(--muted); font-size: 12px; }
    .hidden { display: none; }
    @media (max-width: 920px) {
      .app { grid-template-columns: 1fr; }
      .rail { display: none; }
      .detail { grid-template-columns: 1fr; overflow: auto; }
      .review-panel { border-left: 0; border-top: 1px solid var(--border); }
    }
  </style>
</head>
<body data-theme="system">
  <div class="app">
    <aside class="rail">
      <div class="brand"><div class="brand-mark" id="brandMark"></div><div>Asset Review</div></div>
      <input id="search" class="search" placeholder="Search assets" />
      <div id="meta" class="meta"></div>
      <div id="folders" class="folder-list"></div>
    </aside>
    <main class="main">
      <header class="toolbar">
        <h1 id="title">Asset Review</h1>
        <div class="toolbar-actions">
          <button id="reloadBtn" class="reload-button" title="Reload all assets from disk"></button>
          <button id="clearReviewsBtn" class="clear-button" title="Clear review contents except approved decisions"></button>
          <div class="segmented" title="Status filter">
            <button data-filter="all" class="active">All</button>
            <button data-filter="open">Open</button>
            <button data-filter="approved">Approved</button>
            <button data-filter="refinement">Refine</button>
          </div>
        </div>
      </header>
      <section class="content">
        <div id="gridView" class="grid-view"><div id="assetGrid" class="asset-grid"></div></div>
        <div id="detailView" class="detail hidden">
          <div class="stage-wrap">
            <div class="detail-nav">
              <div class="icon-row">
                <button id="backBtn" class="icon-button" title="Back to grid"></button>
                <button id="prevBtn" class="icon-button" title="Previous asset"></button>
                <button id="nextBtn" class="icon-button" title="Next asset"></button>
              </div>
              <div class="detail-controls">
                <div class="zoom-control">
                  <span>Zoom</span>
                  <input id="zoomSlider" type="range" min="0.25" max="8" step="0.25" value="2" title="Zoom" />
                  <span id="zoomValue">200%</span>
                </div>
                <div id="palette" class="palette" title="C64 background palette">
                </div>
              </div>
            </div>
            <div class="stage">
              <div class="context-bar"><span id="contextLabel"></span><span id="assetCount"></span></div>
              <div id="preview" class="preview"><img id="detailImage" alt="" /></div>
              <div id="animPlayer" class="anim-player hidden">
                <div class="anim-row">
                  <strong id="animCharacterLabel" style="font-size:13px;"></strong>
                  <span id="animKindBadge" class="badge"></span>
                </div>
                <div id="animSeqButtons" class="anim-seq-buttons" title="Select animation sequence"></div>
                <div class="anim-row">
                  <button id="animPlayBtn" class="anim-play-btn" type="button" title="Play or pause (Space)">Play</button>
                  <label><input id="animLoop" type="checkbox" checked /> Loop</label>
                  <label>FPS <input id="animFps" type="number" min="1" max="24" value="8" style="width:52px;border:1px solid var(--border);border-radius:6px;padding:4px 6px;background:var(--surface);color:var(--text);" /></label>
                  <span id="animFrameMeta" class="anim-meta"></span>
                </div>
                <div class="anim-meta">Space: play/pause · Up/Down: prev/next asset · Left/Right: prev/next frame (pauses) · sequences auto-loop walk/idle</div>
              </div>
            </div>
          </div>
          <aside class="review-panel">
            <div>
              <h2 id="detailName"></h2>
              <div id="detailPath" class="detail-path"></div>
            </div>
            <div id="detailStatus" class="status">Open</div>
            <textarea id="comment" placeholder="Approval notes or requested refinements"></textarea>
            <div class="primary-actions">
              <button id="approveBtn" class="action approve">Approve</button>
              <button id="refineBtn" class="action refine">Request refinement</button>
            </div>
            <div id="toast" class="toast"></div>
          </aside>
        </div>
      </section>
    </main>
  </div>
  <script>
    const icons = {
      logo: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/><path d="M14 17h7M17.5 13.5v7"/></svg>',
      grid: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/></svg>',
      prev: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><path d="M15 18l-6-6 6-6"/></svg>',
      next: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><path d="M9 6l6 6-6 6"/></svg>',
      reload: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><path d="M21 12a9 9 0 1 1-2.64-6.36"/><path d="M21 3v6h-6"/></svg>',
      clear: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 14H6L5 6"/><path d="M10 11v5"/><path d="M14 11v5"/></svg>'
    };
    brandMark.innerHTML = icons.logo;
    reloadBtn.innerHTML = `${icons.reload}<span>Reload All</span>`;
    clearReviewsBtn.innerHTML = `${icons.clear}<span>Clear Reviews</span>`;
    backBtn.innerHTML = icons.grid;
    prevBtn.innerHTML = icons.prev;
    nextBtn.innerHTML = icons.next;

    const systemTheme = window.matchMedia('(prefers-color-scheme: dark)');
    function assetReviewSetTheme(theme) {
      const normalized = theme === 'dark' ? 'dark' : theme === 'light' ? 'light' : (systemTheme.matches ? 'dark' : 'light');
      document.body.dataset.theme = normalized;
    }
    window.assetReviewSetTheme = assetReviewSetTheme;
    const queryTheme = new URLSearchParams(window.location.search).get('theme');
    assetReviewSetTheme(queryTheme || 'system');
    systemTheme.addEventListener?.('change', event => {
      if (!new URLSearchParams(window.location.search).has('theme')) {
        assetReviewSetTheme(event.matches ? 'dark' : 'light');
      }
    });

    const c64Palette = [
      { name: 'Black', hex: '#000000' },
      { name: 'White', hex: '#ffffff' },
      { name: 'Red', hex: '#883932' },
      { name: 'Cyan', hex: '#67b6bd' },
      { name: 'Purple', hex: '#8b3f96' },
      { name: 'Green', hex: '#55a049' },
      { name: 'Blue', hex: '#40318d' },
      { name: 'Yellow', hex: '#bfce72' },
      { name: 'Orange', hex: '#8b5429' },
      { name: 'Brown', hex: '#574200' },
      { name: 'Light Red', hex: '#b86962' },
      { name: 'Dark Gray', hex: '#505050' },
      { name: 'Gray', hex: '#787878' },
      { name: 'Light Green', hex: '#94e089' },
      { name: 'Light Blue', hex: '#7869c4' },
      { name: 'Light Gray', hex: '#9f9f9f' }
    ];

    let config, index, assets = [], folderRecords = [], selectedFolder = null, selected = -1, filter = 'all';
    let assetVersion = Date.now();
    let selectedColor = c64Palette[0], zoom = 2;
    let animationSets = [];
    let activeAnimSet = null;
    let activeSequence = null;
    let animFrameIndex = 0;
    let animPlaying = false;
    let animTimer = null;

    async function init() {
      config = await (await fetch('/api/config')).json();
      title.textContent = config.title;
      await loadAssets();
      renderPalette();
      applyPreviewSettings();
      renderFolders();
      renderGrid();
    }

    async function loadAssets() {
      index = await (await fetch('/api/assets', { cache: 'no-store' })).json();
      assets = index.assets;
      folderRecords = index.folders;
      try {
        animationSets = await (await fetch('/api/animation-sets', { cache: 'no-store' })).json();
      } catch {
        animationSets = [];
      }
      const manifestLabel = index.manifestFiles.length === 0
        ? 'Manifest: none'
        : `Manifest: ${index.manifestFiles.length} file${index.manifestFiles.length === 1 ? '' : 's'}`;
      const animLabel = animationSets.length > 0
        ? `<br>Animation sets: ${animationSets.length}`
        : '';
      meta.innerHTML = `${assets.length} assets<br>${escapeHtml(index.assetRoot)}<br>${escapeHtml(manifestLabel)}${animLabel}<br>Feedback: ${escapeHtml(index.feedbackFile)}`;
    }

    function filteredAssets() {
      const q = search.value.trim().toLowerCase();
      return assets.filter(a =>
        (!selectedFolder || a.folder === selectedFolder) &&
        (filter === 'all' || (filter === 'open' ? !a.decision : a.decision === filter)) &&
        (!q || a.path.toLowerCase().includes(q))
      );
    }

    function renderFolders() {
      folders.innerHTML = '';
      const all = document.createElement('button');
      all.className = 'folder' + (!selectedFolder ? ' active' : '');
      all.innerHTML = `<span>All folders</span><span class="badge">${assets.length}</span>`;
      all.onclick = () => { selectedFolder = null; renderFolders(); renderGrid(); };
      folders.appendChild(all);
      for (const f of folderRecords) {
        const btn = document.createElement('button');
        btn.className = 'folder' + (selectedFolder === f.path ? ' active' : '');
        btn.innerHTML = `<span>${escapeHtml(f.path)}</span><span class="badge">${f.approved}/${f.refinements}/${f.count}</span>`;
        btn.onclick = () => { selectedFolder = f.path; renderFolders(); renderGrid(); };
        folders.appendChild(btn);
      }
    }

    function renderGrid() {
      const list = filteredAssets();
      assetGrid.innerHTML = '';
      for (const asset of list) {
        const card = document.createElement('button');
        card.className = 'asset-card';
        card.onclick = () => openDetail(assets.indexOf(asset));
        card.innerHTML = `
          <div class="thumb"><img src="${assetUrl(asset.path)}" alt=""></div>
          <div class="asset-info">
            <div class="asset-name">${escapeHtml(asset.name)}</div>
            <div class="asset-folder">${escapeHtml(asset.folder)}</div>
            <div class="status ${asset.decision || ''}">${asset.decision || 'open'}</div>
          </div>`;
        assetGrid.appendChild(card);
      }
    }

    function openDetail(i) {
      if (i < 0 || i >= assets.length) return;
      selected = i;
      gridView.classList.add('hidden');
      detailView.classList.remove('hidden');
      renderDetail();
    }

    function renderDetail() {
      const asset = assets[selected];
      if (!asset) return;
      detailImage.src = assetUrl(asset.path);
      detailImage.alt = asset.name;
      detailName.textContent = asset.name;
      detailPath.textContent = asset.path;
      detailStatus.textContent = asset.decision || 'Open';
      detailStatus.className = `status ${asset.decision || ''}`;
      comment.value = asset.comment || '';
      contextLabel.textContent = `${asset.extension.toUpperCase()} on C64 ${selectedColor.name}`;
        const filtered = filteredAssets();
        const filteredIndex = filtered.findIndex(a => a.path === asset.path);
        assetCount.textContent = filteredIndex >= 0
          ? `${filteredIndex + 1} / ${filtered.length}`
          : `${selected + 1} / ${assets.length}`;
      bindAnimationForAsset(asset);
      applyPreviewSettings();
    }

    function findAnimationSet(assetPath) {
      if (!animationSets || animationSets.length === 0) return null;
      const path = String(assetPath).replace(/\\/g, '/');
      // Index paths are often prefixed (docs/Project/Art-Source/full-refresh/...).
      // characterKey is always "hero-combat/<slug>" or "monster-combat/<slug>".
      const keyMatch = path.match(/(?:^|\/)((?:hero|monster)-combat\/[^/]+?)(?:\.png$|\/)/i);
      const keyFromPath = keyMatch ? keyMatch[1] : null;
      return animationSets.find(set => {
        const key = String(set.characterKey || '');
        if (!key) return false;
        if (path === set.primaryPath) return true;
        // Short or absolute flat primary: hero-combat/slug.png OR .../hero-combat/slug.png
        if (path === key + '.png' || path.endsWith('/' + key + '.png')) return true;
        // Any state frame under the character folder: .../hero-combat/slug/walk-1.png
        if (path.includes('/' + key + '/') || path.startsWith(key + '/')) return true;
        if (keyFromPath && keyFromPath.toLowerCase() === key.toLowerCase()) return true;
        return (set.sequences || []).some(seq => (seq.frames || []).includes(path));
      }) || null;
    }

    function stopAnimationTimer() {
      if (animTimer) {
        clearInterval(animTimer);
        animTimer = null;
      }
      animPlaying = false;
      if (animPlayBtn) {
        animPlayBtn.textContent = 'Play';
        animPlayBtn.classList.remove('playing');
      }
    }

    function currentAnimFrames() {
      return activeSequence && activeSequence.frames ? activeSequence.frames : [];
    }

    function showAnimFrame(index) {
      const frames = currentAnimFrames();
      if (!frames.length) return;
      animFrameIndex = ((index % frames.length) + frames.length) % frames.length;
      const framePath = frames[animFrameIndex];
      detailImage.src = assetUrl(framePath);
      detailImage.alt = framePath.split('/').pop();
      if (animFrameMeta) {
        animFrameMeta.textContent = `${activeSequence.label}: ${animFrameIndex + 1}/${frames.length} · ${framePath.split('/').pop()}`;
      }
      // Keep review panel path on the currently displayed frame when possible.
      const match = assets.findIndex(a => a.path === framePath);
      if (match >= 0) {
        selected = match;
        const asset = assets[selected];
        detailName.textContent = asset.name;
        detailPath.textContent = asset.path;
        detailStatus.textContent = asset.decision || 'Open';
        detailStatus.className = `status ${asset.decision || ''}`;
        comment.value = asset.comment || '';
      }
      applyPreviewSettings();
    }

    function startAnimationTimer() {
      stopAnimationTimer();
      const frames = currentAnimFrames();
      if (frames.length <= 1) {
        showAnimFrame(0);
        return;
      }
      const fps = Math.max(1, Math.min(24, Number(animFps.value) || activeSequence.fps || 8));
      animPlaying = true;
      animPlayBtn.textContent = 'Pause';
      animPlayBtn.classList.add('playing');
      animTimer = setInterval(() => {
        const loop = animLoop.checked;
        if (animFrameIndex + 1 >= frames.length) {
          if (loop) {
            showAnimFrame(0);
          } else {
            stopAnimationTimer();
            showAnimFrame(frames.length - 1);
          }
          return;
        }
        showAnimFrame(animFrameIndex + 1);
      }, Math.round(1000 / fps));
    }

    function toggleAnimationPlayback() {
      if (!activeSequence || currentAnimFrames().length === 0) return;
      if (animPlaying) {
        stopAnimationTimer();
      } else {
        // Restart from first frame when resuming a finished one-shot at the end.
        const frames = currentAnimFrames();
        if (!animLoop.checked && animFrameIndex >= frames.length - 1 && frames.length > 1) {
          animFrameIndex = 0;
          showAnimFrame(0);
        }
        startAnimationTimer();
      }
    }

    /// Step one frame in the active sequence. Always pauses playback first.
    function stepAnimationFrame(delta) {
      const frames = currentAnimFrames();
      if (!frames.length) return;
      if (animPlaying) stopAnimationTimer();
      showAnimFrame(animFrameIndex + delta);
    }

    function selectSequence(sequenceId, { autoplay = true } = {}) {
      if (!activeAnimSet) return;
      const seq = (activeAnimSet.sequences || []).find(s => s.id === sequenceId)
        || (activeAnimSet.sequences || [])[0];
      if (!seq) return;
      activeSequence = seq;
      animLoop.checked = !!seq.loopDefault;
      animFps.value = String(seq.fps || 8);
      [...animSeqButtons.querySelectorAll('button')].forEach(btn => {
        btn.classList.toggle('active', btn.dataset.seqId === seq.id);
      });
      stopAnimationTimer();
      showAnimFrame(0);
      if (autoplay && (seq.frames || []).length > 1) {
        startAnimationTimer();
      }
    }

    function bindAnimationForAsset(asset) {
      stopAnimationTimer();
      activeAnimSet = findAnimationSet(asset.path);
      activeSequence = null;
      animFrameIndex = 0;
      if (!activeAnimSet || !(activeAnimSet.sequences || []).length) {
        animPlayer.classList.add('hidden');
        return;
      }

      animPlayer.classList.remove('hidden');
      animCharacterLabel.textContent = activeAnimSet.slug;
      animKindBadge.textContent = activeAnimSet.kind;
      animSeqButtons.innerHTML = '';
      for (const seq of activeAnimSet.sequences) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.dataset.seqId = seq.id;
        btn.textContent = seq.label;
        btn.title = `${seq.label} (${(seq.frames || []).length} frame${(seq.frames || []).length === 1 ? '' : 's'})`;
        btn.onclick = () => selectSequence(seq.id, { autoplay: true });
        animSeqButtons.appendChild(btn);
      }

      // Prefer sequence matching current frame; else Walk if present; else first.
      const path = asset.path.replace(/\\/g, '/');
      let preferred = (activeAnimSet.sequences || []).find(seq => (seq.frames || []).includes(path));
      if (!preferred) preferred = (activeAnimSet.sequences || []).find(seq => seq.id === 'walk');
      if (!preferred) preferred = (activeAnimSet.sequences || [])[0];
      selectSequence(preferred.id, { autoplay: (preferred.frames || []).length > 1 && preferred.id === 'walk' });
    }

    async function reloadAll() {
      const selectedPath = selected >= 0 && assets[selected] ? assets[selected].path : null;
      const wasDetail = !detailView.classList.contains('hidden');
      reloadBtn.disabled = true;
      reloadBtn.innerHTML = `${icons.reload}<span>Reloading</span>`;
      try {
        await loadAssets();
        assetVersion = Date.now();
        if (selectedFolder && !folderRecords.some(f => f.path === selectedFolder)) {
          selectedFolder = null;
        }

        selected = selectedPath ? assets.findIndex(asset => asset.path === selectedPath) : -1;
        if (selected < 0 && wasDetail && assets.length > 0) {
          selected = 0;
        }

        renderFolders();
        renderGrid();
        if (wasDetail && selected >= 0) {
          gridView.classList.add('hidden');
          detailView.classList.remove('hidden');
          renderDetail();
        } else {
          detailView.classList.add('hidden');
          gridView.classList.remove('hidden');
        }

        toast.textContent = `Reloaded ${assets.length} assets.`;
      } catch {
        toast.textContent = 'Could not reload assets.';
      } finally {
        reloadBtn.disabled = false;
        reloadBtn.innerHTML = `${icons.reload}<span>Reload All</span>`;
      }
    }

    async function clearReviewsExceptApprovals() {
      if (!confirm('Clear all review contents except approvals? Refinement notes and non-approved decisions will be removed from the feedback file.')) {
        return;
      }

      clearReviewsBtn.disabled = true;
      clearReviewsBtn.innerHTML = `${icons.clear}<span>Clearing</span>`;
      try {
        const response = await fetch('/api/feedback/clear-non-approvals', { method: 'POST' });
        if (!response.ok) {
          toast.textContent = 'Could not clear review contents.';
          return;
        }

        const result = await response.json();
        await reloadAll();
        toast.textContent = `Cleared non-approved review contents. Kept ${result.keptApprovals} approvals.`;
      } catch {
        toast.textContent = 'Could not clear review contents.';
      } finally {
        clearReviewsBtn.disabled = false;
        clearReviewsBtn.innerHTML = `${icons.clear}<span>Clear Reviews</span>`;
      }
    }

    async function submit(decision) {
      const asset = assets[selected];
      if (!asset) return;
      const body = { assetPath: asset.path, decision, comment: comment.value, context: reviewContext() };
      const response = await fetch('/api/feedback', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      });
      if (!response.ok) { toast.textContent = 'Could not write feedback.'; return; }
      const entry = await response.json();
      asset.decision = entry.decision;
      asset.comment = entry.comment;
      asset.timestampUtc = entry.timestampUtc;
      toast.textContent = `Saved ${entry.decision} feedback.`;
      renderDetail();
      renderFolders();
      renderGrid();
    }

    function navigate(delta) {
      stopAnimationTimer();
      const list = filteredAssets();
      if (list.length === 0) return;

      const current = assets[selected];
      let filteredIndex = current ? list.findIndex(a => a.path === current.path) : -1;
      if (filteredIndex < 0) {
        filteredIndex = delta >= 0 ? -1 : 0;
      }

      const next = list[(filteredIndex + delta + list.length) % list.length];
      selected = assets.indexOf(next);
      renderDetail();
    }

    function renderPalette() {
      palette.innerHTML = '';
      for (const color of c64Palette) {
        const button = document.createElement('button');
        button.className = 'swatch' + (color === selectedColor ? ' active' : '');
        button.type = 'button';
        button.title = color.name;
        button.setAttribute('aria-label', color.name);
        button.style.background = color.hex;
        button.onclick = () => {
          selectedColor = color;
          applyPreviewSettings();
          renderGrid();
        };
        palette.appendChild(button);
      }
    }

    function setZoom(value) {
      zoom = Math.min(8, Math.max(0.25, Math.round(Number(value) * 4) / 4));
      applyPreviewSettings();
    }

    function applyPreviewSettings() {
      document.documentElement.style.setProperty('--asset-bg', selectedColor.hex);
      if (zoomSlider) zoomSlider.value = String(zoom);
      if (zoomValue) zoomValue.textContent = `${Math.round(zoom * 100)}%`;
      if (detailImage && detailImage.naturalWidth > 0) {
        detailImage.style.width = `${Math.round(detailImage.naturalWidth * zoom)}px`;
        detailImage.style.height = 'auto';
      }
      if (palette) {
        [...palette.children].forEach((button, index) => button.classList.toggle('active', c64Palette[index] === selectedColor));
      }
      if (selected >= 0 && assets[selected]) {
        contextLabel.textContent = `${assets[selected].extension.toUpperCase()} on C64 ${selectedColor.name}`;
      }
    }

    function reviewContext() {
      return `background=${selectedColor.name}; zoom=${Math.round(zoom * 100)}%`;
    }

    function assetUrl(path) {
      return '/assets/' + path.split('/').map(encodeURIComponent).join('/') + '?v=' + assetVersion;
    }

    function escapeHtml(s) {
      return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }

    search.oninput = renderGrid;
    document.querySelectorAll('[data-filter]').forEach(b => b.onclick = () => {
      filter = b.dataset.filter;
      document.querySelectorAll('[data-filter]').forEach(x => x.classList.toggle('active', x === b));
      renderGrid();
    });
    zoomSlider.oninput = () => setZoom(Number(zoomSlider.value));
    detailImage.onload = applyPreviewSettings;
    preview.addEventListener('wheel', e => {
      if (detailView.classList.contains('hidden')) return;
      e.preventDefault();
      setZoom(zoom + (e.deltaY < 0 ? 0.25 : -0.25));
    }, { passive: false });
    backBtn.onclick = () => {
      stopAnimationTimer();
      detailView.classList.add('hidden');
      gridView.classList.remove('hidden');
      renderGrid();
    };
    reloadBtn.onclick = reloadAll;
    clearReviewsBtn.onclick = clearReviewsExceptApprovals;
    prevBtn.onclick = () => navigate(-1);
    nextBtn.onclick = () => navigate(1);
    approveBtn.onclick = () => submit('approved');
    refineBtn.onclick = () => submit('refinement');
    animPlayBtn.onclick = () => toggleAnimationPlayback();
    animLoop.onchange = () => {
      if (animPlaying) startAnimationTimer();
    };
    animFps.onchange = () => {
      if (animPlaying) startAnimationTimer();
    };
    document.addEventListener('keydown', e => {
      if (detailView.classList.contains('hidden')) return;
      const el = document.activeElement;
      const typing = el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable);
      if (typing) return;
      // Up/Down: previous/next asset in the filtered list.
      if (e.key === 'ArrowUp') { e.preventDefault(); navigate(-1); return; }
      if (e.key === 'ArrowDown') { e.preventDefault(); navigate(1); return; }
      // Left/Right: step animation frame; always pause if currently playing.
      if (e.key === 'ArrowLeft') { e.preventDefault(); stepAnimationFrame(-1); return; }
      if (e.key === 'ArrowRight') { e.preventDefault(); stepAnimationFrame(1); return; }
      if (e.key === ' ' || e.code === 'Space') {
        e.preventDefault();
        toggleAnimationPlayback();
        return;
      }
      if (e.key === 'Escape') backBtn.click();
    });
    init();
  </script>
</body>
</html>
""";
}

internal sealed record ReviewOptions(
    string Workspace,
    string AssetRoot,
    string FeedbackFile,
    int Port,
    string Title,
    ReviewLaunchMode LaunchMode)
{
    public string FeedbackAssetRoot { get; init; } = AssetRoot;
    public string[] ManifestFiles { get; init; } = [];

    public string ReviewUrl => $"http://127.0.0.1:{Port}/";

    public static ReviewOptions Parse(string[] args)
    {
        string workspace = Directory.GetCurrentDirectory();
        string? assetRoot = null;
        string? feedbackFile = null;
        int port = 5087;
        string title = "Asset Review";
        var launchMode = ReviewLaunchMode.Embedded;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} requires a value.");
            switch (arg)
            {
                case "--workspace":
                    workspace = Path.GetFullPath(Next());
                    break;
                case "--asset-root":
                    assetRoot = Path.GetFullPath(Next());
                    break;
                case "--feedback-file":
                    feedbackFile = Path.GetFullPath(Next());
                    break;
                case "--port":
                    if (!Program.TryParsePort(Next(), out port, out var portError))
                    {
                        Console.Error.WriteLine(portError);
                        Environment.Exit(2);
                    }

                    break;
                case "--title":
                    title = Next();
                    break;
                case "--no-open":
                case "--server-only":
                    launchMode = ReviewLaunchMode.ServerOnly;
                    break;
                case "--external-browser":
                case "--open-browser":
                    launchMode = ReviewLaunchMode.ExternalBrowser;
                    break;
                case "--embedded":
                    launchMode = ReviewLaunchMode.Embedded;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                case "--version":
                case "-v":
                    Console.WriteLine(Program.VersionText);
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        workspace = Path.GetFullPath(workspace);
        assetRoot = Path.GetFullPath(assetRoot ?? workspace);
        string[] manifestFiles = Program.DiscoverManifestFiles(workspace, assetRoot);
        feedbackFile = Path.GetFullPath(feedbackFile ?? Program.DiscoverFeedbackFile(workspace, assetRoot, manifestFiles));
        string feedbackAssetRoot = Program.ResolveFeedbackAssetRoot(workspace, assetRoot, feedbackFile, manifestFiles);

        return new ReviewOptions(workspace, assetRoot, feedbackFile, port, title, launchMode)
        {
            FeedbackAssetRoot = feedbackAssetRoot,
            ManifestFiles = manifestFiles,
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        asset-review

        Launches a local PNG/SVG asset review app for the current workspace.

        Options:
          --workspace <path>       Workspace root. Defaults to current directory.
          --asset-root <path>      Folder to scan for .png and .svg files. Defaults to workspace.
          --feedback-file <path>   JSONL feedback output. Defaults to .asset-review/feedback.jsonl.
          --port <number>          Localhost port. Defaults to 5087.
          --title <text>           App title.
          --embedded               Launch the embedded Avalonia/WebView2 app (default).
          --no-open, --server-only Start the local server without opening a UI.
          --external-browser       Open the system browser instead of the embedded app.
          --version                Print the installed tool version and exit.
        """);
    }
}

public enum ReviewLaunchMode
{
    Embedded,
    ExternalBrowser,
    ServerOnly,
}

internal sealed record AssetIndex(
    string Workspace,
    string AssetRoot,
    string FeedbackFile,
    string FeedbackAssetRoot,
    string[] ManifestFiles,
    AssetRecord[] Assets,
    FolderRecord[] Folders);
internal sealed record FolderRecord(string Path, int Count, int Approved, int Refinements);
internal sealed record AssetRecord(string Path, string Folder, string Name, string Extension, long Bytes, string? Decision, string? Comment, DateTimeOffset? TimestampUtc);
internal sealed record FeedbackRequest(string AssetPath, string Decision, string? Comment, string? Context);
internal sealed record FeedbackEntry(DateTimeOffset TimestampUtc, string AssetPath, string Decision, string Comment, string Context);
