using System.Text;
using System.Text.RegularExpressions;

namespace SharpNinja.AssetReview.Tool;

/// <summary>
/// Reads explicit storyboard sequences from <c>_manifest.md</c>.
/// A sequence is an ATX heading of the form <c>Sequence: Name</c> or <c>Storyboard: Name</c>
/// followed by backtick-wrapped frame paths, in order, until the next heading.
/// </summary>
internal static class StoryboardManifest
{
    private static readonly Regex CommentRegex = new(
        "<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HeadingRegex = new(
        @"^#{1,6}[ \t]+(?<body>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SequenceRegex = new(
        @"^(?:sequence|storyboard)[ \t]*:[ \t]*(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExplicitIdRegex = new(
        @"[ \t]+\{#(?<id>[A-Za-z0-9][A-Za-z0-9_-]*)\}[ \t]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static IReadOnlyList<RawStoryboard> Parse(string manifestText)
    {
        var sequences = new List<RawStoryboard>();
        RawBuilder? current = null;

        foreach (string line in ContentLines(manifestText))
        {
            if (TryReadHeading(line, out bool isSequenceHeading, out string name, out string? explicitId))
            {
                if (current is not null)
                {
                    sequences.Add(current.Build());
                    current = null;
                }

                if (isSequenceHeading)
                    current = new RawBuilder(name, explicitId);
                continue;
            }

            if (current is null)
                continue;

            foreach (string path in ExtractAssetPaths(line))
                current.Frames.Add(path);
        }

        if (current is not null)
            sequences.Add(current.Build());

        return sequences;
    }

    internal static IReadOnlyList<StoryboardSequence> Load(ReviewOptions options, IReadOnlyList<string> manifestFiles)
    {
        var pending = new List<(RawStoryboard Sequence, string ManifestDirectory)>();
        foreach (string manifestFile in manifestFiles)
        {
            string? directory = Path.GetDirectoryName(manifestFile);
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string text;
            try
            {
                text = File.ReadAllText(manifestFile);
            }
            catch
            {
                continue;
            }

            foreach (RawStoryboard sequence in Parse(text))
                pending.Add((sequence, directory));
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<StoryboardSequence>(pending.Count);
        foreach ((RawStoryboard sequence, string directory) in pending)
        {
            var frames = new List<string>(sequence.FramePaths.Count);
            foreach (string framePath in sequence.FramePaths)
            {
                string? relative = TryResolveFrame(options, directory, framePath);
                if (relative is not null)
                    frames.Add(relative);
            }

            if (frames.Count == 0)
                continue;

            string id = AllocateId(usedIds, sequence.ExplicitId ?? Slug(sequence.Name));
            resolved.Add(new StoryboardSequence(id, sequence.Name, frames));
        }

        return resolved;
    }

    /// <summary>
    /// Backtick-wrapped <c>.png</c>/<c>.svg</c> paths on a single manifest line, in left-to-right order.
    /// </summary>
    internal static IEnumerable<string> ExtractAssetPaths(string line)
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

    internal static string? TryResolveFrame(ReviewOptions options, string manifestDirectory, string manifestRelativePath)
    {
        if (string.IsNullOrWhiteSpace(manifestRelativePath))
            return null;

        string absolute = Path.GetFullPath(Path.Combine(
            manifestDirectory,
            Program.NormalizeRelativePath(manifestRelativePath)));
        if (!Program.IsInside(options.AssetRoot, absolute) ||
            !File.Exists(absolute) ||
            !Program.IsReviewAssetCandidate(options, absolute))
        {
            return null;
        }

        return Path.GetRelativePath(options.AssetRoot, absolute).Replace('\\', '/');
    }

    internal static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool pendingHyphen = false;
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingHyphen && sb.Length > 0)
                    sb.Append('-');
                pendingHyphen = false;
                sb.Append(c);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return sb.Length == 0 ? "sequence" : sb.ToString();
    }

    internal static void AppendSequences(StringBuilder sb, IReadOnlyList<RawStoryboard> sequences)
    {
        if (sequences.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("## Storyboards");
        sb.AppendLine();
        foreach (RawStoryboard sequence in sequences)
        {
            sb.Append("### Sequence: ");
            sb.Append(sequence.Name);
            if (!string.IsNullOrEmpty(sequence.ExplicitId))
            {
                sb.Append(" {#");
                sb.Append(sequence.ExplicitId);
                sb.Append('}');
            }

            sb.AppendLine();
            sb.AppendLine();
            foreach (string frame in sequence.FramePaths)
                sb.AppendLine($"- `{frame}`");
            sb.AppendLine();
        }
    }

    private static string AllocateId(HashSet<string> usedIds, string baseId)
    {
        string id = string.IsNullOrWhiteSpace(baseId) ? "sequence" : baseId.Trim();
        if (usedIds.Add(id))
            return id;

        for (int n = 2; ; n++)
        {
            string candidate = $"{id}-{n}";
            if (usedIds.Add(candidate))
                return candidate;
        }
    }

    private static bool TryReadHeading(string line, out bool isSequenceHeading, out string name, out string? explicitId)
    {
        isSequenceHeading = false;
        name = string.Empty;
        explicitId = null;

        Match heading = HeadingRegex.Match(line.Trim());
        if (!heading.Success)
            return false;

        string body = heading.Groups["body"].Value.Trim();
        Match sequence = SequenceRegex.Match(body);
        if (!sequence.Success)
            return true;

        string rawName = sequence.Groups["name"].Value.Trim();
        Match id = ExplicitIdRegex.Match(rawName);
        if (id.Success)
        {
            explicitId = id.Groups["id"].Value;
            rawName = rawName[..id.Index].Trim();
        }

        if (string.IsNullOrWhiteSpace(rawName))
            return true;

        isSequenceHeading = true;
        name = rawName;
        return true;
    }

    private static IEnumerable<string> ContentLines(string manifestText)
    {
        string withoutComments = CommentRegex.Replace(manifestText, string.Empty);
        bool inFence = false;
        char fenceChar = '\0';
        foreach (string raw in withoutComments.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                char marker = trimmed[0];
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = marker;
                    continue;
                }

                if (marker == fenceChar)
                {
                    inFence = false;
                    fenceChar = '\0';
                    continue;
                }
            }

            if (inFence)
                continue;

            yield return line;
        }
    }

    private sealed class RawBuilder(string name, string? explicitId)
    {
        public string Name { get; } = name;
        public string? ExplicitId { get; } = explicitId;
        public List<string> Frames { get; } = [];
        public RawStoryboard Build() => new(Name, ExplicitId, Frames.ToArray());
    }
}

internal sealed record RawStoryboard(string Name, string? ExplicitId, IReadOnlyList<string> FramePaths);

internal sealed record StoryboardSequence(string Id, string Name, IReadOnlyList<string> Frames);
