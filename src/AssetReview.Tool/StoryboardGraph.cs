using System.Text.RegularExpressions;

namespace SharpNinja.AssetReview.Tool;

/// <summary>
/// Parses and validates explicit frame IDs and transitions in one storyboard manifest section.
/// 2026-11-23: FR-ASSETREVIEW-018 and TR-ASSETREVIEW-STORYBOARD-001, AC-authored-transitions
/// and AC-diagnostics define graph loading and source-located errors.
/// </summary>
internal sealed class StoryboardGraphDefinition
{
    private static readonly Regex FrameLine = new(
        @"^[ \t]*-[ \t]+`(?<path>[^`]+)`[ \t]+\{#(?<id>[^}]*)\}[ \t]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TransitionLine = new(
        @"^(?<indent>[ \t]*)-[ \t]+(?<kind>Next|Branch|Loop)[ \t]*:[ \t]*(?<value>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FrameId = new(
        @"\A[A-Za-z0-9][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly List<FrameDefinition> _frames = [];
    private readonly List<TransitionDefinition> _transitions = [];
    private readonly List<Issue> _issues = [];

    private StoryboardGraphDefinition(int headingLine) => HeadingLine = headingLine;

    private int HeadingLine { get; }

    internal static bool IsTransitionLine(string line) => TransitionLine.IsMatch(line);

    internal static StoryboardGraphDefinition? Parse(IReadOnlyList<StoryboardManifestLine> lines, int headingLine)
    {
        if (!lines.Any(line => FrameLine.IsMatch(line.Text) || IsTransitionLine(line.Text)))
            return null;

        var graph = new StoryboardGraphDefinition(headingLine);
        string? activeFrameId = null;
        foreach (StoryboardManifestLine line in lines)
        {
            Match transition = TransitionLine.Match(line.Text);
            if (transition.Success)
            {
                graph.AddTransition(transition, activeFrameId, line.Number);
                continue;
            }

            Match frame = FrameLine.Match(line.Text);
            if (frame.Success)
            {
                string id = frame.Groups["id"].Value;
                string path = frame.Groups["path"].Value.Trim();
                activeFrameId = FrameId.IsMatch(id) ? id : null;
                if (activeFrameId is null)
                    graph._issues.Add(new Issue("invalid-frame-id", line.Number, $"Frame ID '{id}' is invalid."));
                else
                    graph._frames.Add(new FrameDefinition(activeFrameId, path, line.Number));
                continue;
            }

            if (StoryboardManifest.ExtractAssetPaths(line.Text).Any())
            {
                activeFrameId = null;
                graph._issues.Add(new Issue("missing-frame-id", line.Number, "Every frame in this storyboard needs a frame ID."));
            }
        }

        return graph;
    }

    private void AddTransition(Match directive, string? sourceId, int line)
    {
        string indent = directive.Groups["indent"].Value;
        if (sourceId is null || (indent.Length < 2 && !indent.Contains('\t')))
        {
            _issues.Add(new Issue("orphan-transition", line, "Indent the transition under a frame with an ID."));
            return;
        }

        string kind = directive.Groups["kind"].Value.ToLowerInvariant();
        string target = directive.Groups["value"].Value.Trim();
        string? label = null;
        if (kind != "next")
        {
            int arrow = target.LastIndexOf("->", StringComparison.Ordinal);
            if (arrow > 0)
            {
                label = target[..arrow].Trim();
                target = target[(arrow + 2)..].Trim();
            }
        }

        if (!FrameId.IsMatch(target) || (kind != "next" && string.IsNullOrEmpty(label)))
        {
            string syntax = kind == "next" ? "Next: frame-id" : $"{directive.Groups["kind"].Value}: Label -> frame-id";
            _issues.Add(new Issue("invalid-transition", line, $"Expected '{syntax}'."));
            return;
        }

        _transitions.Add(new TransitionDefinition(sourceId, target, kind, label, line));
    }

    internal StoryboardGraph Resolve(ReviewOptions options, string manifestPath, string directory)
    {
        var issues = new List<Issue>(_issues);
        var declaredFrames = new Dictionary<string, FrameDefinition>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<StoryboardGraphNode>();
        foreach (FrameDefinition frame in _frames)
        {
            if (!declaredFrames.TryAdd(frame.Id, frame))
            {
                issues.Add(new Issue("duplicate-frame-id", frame.Line, $"Frame ID '{frame.Id}' is already declared."));
                continue;
            }

            string? path;
            try
            {
                path = StoryboardManifest.TryResolveFrame(options, directory, frame.Path);
            }
            catch (ArgumentException)
            {
                path = null;
            }

            if (path is null)
                issues.Add(new Issue("missing-frame-file", frame.Line, $"Frame '{frame.Id}' has an unavailable PNG or SVG file: '{frame.Path}'."));
            else
                nodes.Add(new StoryboardGraphNode(frame.Id, path));
        }

        var transitions = new List<StoryboardGraphTransition>();
        var nextSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TransitionDefinition transition in _transitions)
        {
            if (!declaredFrames.TryGetValue(transition.To, out FrameDefinition? target))
            {
                issues.Add(new Issue("unknown-target", transition.Line, $"No frame with ID '{transition.To}' is declared in this storyboard."));
                continue;
            }

            if (transition.Kind == "next" && !nextSources.Add(transition.From))
            {
                issues.Add(new Issue("duplicate-next", transition.Line, $"Frame '{transition.From}' already has a Next transition."));
                continue;
            }

            transitions.Add(new StoryboardGraphTransition(transition.From, target.Id, transition.Kind, transition.Label));
        }

        string? entry = _frames.FirstOrDefault()?.Id;
        if (issues.Count == 0 && entry is not null)
            ValidateTopology(entry, declaredFrames, transitions, issues);

        var diagnostics = issues.OrderBy(issue => issue.Line).Select(issue =>
            new StoryboardGraphDiagnostic(manifestPath, issue.Line, issue.Code, issue.Message)).ToArray();
        return new StoryboardGraph(entry, nodes, diagnostics.Length == 0 ? transitions : [], diagnostics);
    }

    private void ValidateTopology(
        string entry,
        Dictionary<string, FrameDefinition> frames,
        List<StoryboardGraphTransition> transitions,
        List<Issue> issues)
    {
        var forward = frames.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (StoryboardGraphTransition transition in transitions.Where(edge => edge.Kind != "loop"))
            forward[transition.FromFrameId].Add(transition.ToFrameId);

        if (HasCycle(forward))
        {
            issues.Add(new Issue("forward-cycle", HeadingLine, "A cycle in Next or Branch transitions must be closed by a labeled Loop transition."));
            return;
        }

        var reachable = ReachableFrom(entry, forward);
        foreach (FrameDefinition frame in frames.Values.Where(frame => !reachable.Contains(frame.Id)))
            issues.Add(new Issue("unreachable-frame", frame.Line, $"Frame '{frame.Id}' cannot be reached from the first frame by Next or Branch transitions."));

        foreach (TransitionDefinition loop in _transitions.Where(edge => edge.Kind == "loop"))
        {
            if (!ReachableFrom(loop.To, forward).Contains(loop.From))
                issues.Add(new Issue("invalid-loop-target", loop.Line, $"Loop '{loop.Label}' must return to its own frame or to a frame with a forward path to '{loop.From}'."));
        }
    }

    private static bool HasCycle(Dictionary<string, List<string>> forward)
    {
        var incoming = forward.Keys.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (List<string> targets in forward.Values)
        foreach (string target in targets)
            incoming[target]++;

        var ready = new Queue<string>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        int visited = 0;
        while (ready.TryDequeue(out string? frame))
        {
            visited++;
            foreach (string target in forward[frame])
            {
                if (--incoming[target] == 0)
                    ready.Enqueue(target);
            }
        }

        return visited != forward.Count;
    }

    private static HashSet<string> ReachableFrom(string start, Dictionary<string, List<string>> forward)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.TryPop(out string? frame))
        {
            if (!visited.Add(frame))
                continue;

            foreach (string target in forward[frame])
                pending.Push(target);
        }

        return visited;
    }

    private sealed record FrameDefinition(string Id, string Path, int Line);
    private sealed record TransitionDefinition(string From, string To, string Kind, string? Label, int Line);
    private sealed record Issue(string Code, int Line, string Message);
}

/// <summary>Resolved manifest graph. Diagnostics indicate that traversal is unavailable.</summary>
internal sealed record StoryboardGraph(
    string? EntryFrameId,
    IReadOnlyList<StoryboardGraphNode> Nodes,
    IReadOnlyList<StoryboardGraphTransition> Transitions,
    IReadOnlyList<StoryboardGraphDiagnostic> Diagnostics);

/// <summary>A frame occurrence, which has its own ID even when it shares an asset path.</summary>
internal sealed record StoryboardGraphNode(string Id, string Path);

/// <summary>A manifest-authored edge from one frame occurrence to another.</summary>
internal sealed record StoryboardGraphTransition(string FromFrameId, string ToFrameId, string Kind, string? Label);

/// <summary>A graph error with its location in the authored manifest.</summary>
internal sealed record StoryboardGraphDiagnostic(string ManifestPath, int Line, string Code, string Message);

/// <summary>A parsed line and its original one-based position in the manifest.</summary>
internal readonly record struct StoryboardManifestLine(int Number, string Text);
