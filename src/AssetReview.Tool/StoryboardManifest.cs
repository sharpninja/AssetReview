using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharpNinja.AssetReview.Tool;

/// <summary>
/// Reads explicit storyboard sequences from <c>_manifest.md</c>.
/// A sequence is an ATX heading of the form <c>Sequence: Name</c> or <c>Storyboard: Name</c>
/// followed by backtick-wrapped frame paths, in order, until the next heading.
/// 2026-11-23: FR-ASSETREVIEW-018 and TR-ASSETREVIEW-STORYBOARD-001, AC-authored-transitions,
/// AC-roundtrip-and-legacy, and AC-diagnostics support graph sections and preserve their authored body.
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
        string[] sourceLines = manifestText.Split('\n');

        foreach (StoryboardManifestLine line in ContentLines(manifestText))
        {
            if (TryReadHeading(line.Text, out bool isSequenceHeading, out string name, out string? explicitId))
            {
                if (current is not null)
                    sequences.Add(current.Build(sourceLines, line.Number));
                current = isSequenceHeading ? new RawBuilder(name, explicitId, line.Number) : null;
                continue;
            }

            current?.Lines.Add(line);
        }

        if (current is not null)
            sequences.Add(current.Build(sourceLines, sourceLines.Length + 1));

        return sequences;
    }

    internal static IReadOnlyList<StoryboardSequence> Load(ReviewOptions options, IReadOnlyList<string> manifestFiles)
    {
        var pending = new List<(RawStoryboard Sequence, string ManifestPath, string ManifestDirectory)>();
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
                pending.Add((sequence, manifestFile, directory));
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<StoryboardSequence>(pending.Count);
        foreach ((RawStoryboard sequence, string manifestPath, string directory) in pending)
        {
            if (sequence.Graph is { } definition)
            {
                StoryboardGraph graph = definition.Resolve(options, manifestPath, directory);
                string graphId = AllocateId(usedIds, sequence.ExplicitId ?? Slug(sequence.Name));
                var graphFrames = graph.Nodes.Select(node => node.Path).ToArray();
                resolved.Add(new StoryboardSequence(graphId, sequence.Name, graphFrames) { Graph = graph });
                continue;
            }

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
        if (StoryboardGraphDefinition.IsTransitionLine(line))
            yield break;

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
            if (sequence.AuthoredGraphLines is { } graphLines)
            {
                foreach (string line in graphLines)
                    sb.AppendLine(line);
                sb.AppendLine();
                continue;
            }

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

    private static IEnumerable<StoryboardManifestLine> ContentLines(string manifestText)
    {
        // Keep one newline for each line inside a comment so diagnostics use the authored line numbers.
        string withoutComments = CommentRegex.Replace(manifestText, match => new string('\n', match.Value.Count(c => c == '\n')));
        bool inFence = false;
        char fenceChar = '\0';
        int lineNumber = 0;
        foreach (string raw in withoutComments.Split('\n'))
        {
            lineNumber++;
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

            yield return new StoryboardManifestLine(lineNumber, line);
        }
    }

    private sealed class RawBuilder(string name, string? explicitId, int headingLine)
    {
        public List<StoryboardManifestLine> Lines { get; } = [];

        public RawStoryboard Build(string[] sourceLines, int nextHeadingLine)
        {
            var frames = Lines.SelectMany(line => ExtractAssetPaths(line.Text)).ToArray();
            var graph = StoryboardGraphDefinition.Parse(Lines, headingLine);
            if (graph is null)
                return new RawStoryboard(name, explicitId, frames);

            int sectionEnd = nextHeadingLine - 1;
            while (sectionEnd > headingLine && string.IsNullOrWhiteSpace(sourceLines[sectionEnd - 1]))
                sectionEnd--;
            var graphLines = sourceLines[(headingLine - 1)..sectionEnd].Select(line => line.TrimEnd('\r')).ToArray();
            return new RawStoryboard(name, explicitId, frames) { Graph = graph, AuthoredGraphLines = graphLines };
        }
    }
}

/// <summary>A parsed storyboard section before resolving frame files relative to the manifest.</summary>
internal sealed record RawStoryboard(string Name, string? ExplicitId, IReadOnlyList<string> FramePaths)
{
    internal StoryboardGraphDefinition? Graph { get; init; }
    internal IReadOnlyList<string>? AuthoredGraphLines { get; init; }
}

/// <summary>A storyboard with its ordered frames and optional explicit transition graph.</summary>
internal sealed record StoryboardSequence(string Id, string Name, IReadOnlyList<string> Frames)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoryboardGraph? Graph { get; init; }
}
