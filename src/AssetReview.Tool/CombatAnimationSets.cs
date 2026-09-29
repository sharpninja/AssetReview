namespace SharpNinja.AssetReview.Tool;

/// <summary>
/// Groups combat art paths into per-character animation sequences (walk, combat, skills).
/// Used by the asset-review detail player for hero-combat/* and monster-combat/* state sets.
/// </summary>
internal static class CombatAnimationSets
{
    private static readonly string[] SharedSequenceOrder =
    [
        "idle",
        "walk",
        "basic-attack",
        "defend",
        "wait",
        "take-damage-light",
        "take-damage-heavy",
        "dodge",
        "block",
        "stunned",
        "death",
    ];

    private static readonly Dictionary<string, string> SharedLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"] = "Idle",
        ["walk"] = "Walk",
        ["basic-attack"] = "Attack",
        ["defend"] = "Defend",
        ["wait"] = "Wait",
        ["take-damage-light"] = "Damage light",
        ["take-damage-heavy"] = "Damage heavy",
        ["dodge"] = "Dodge",
        ["block"] = "Block",
        ["stunned"] = "Stunned",
        ["death"] = "Death",
    };

    public static IReadOnlyList<CharacterAnimationSet> Build(IEnumerable<string> relativeAssetPaths)
    {
        var byCharacter = new Dictionary<string, CharacterBucket>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in relativeAssetPaths)
        {
            string path = raw.Replace('\\', '/');
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryParseCombatPath(path, out string characterKey, out string slug, out string kind, out string stateStem))
                continue;

            if (!byCharacter.TryGetValue(characterKey, out CharacterBucket? bucket))
            {
                bucket = new CharacterBucket(characterKey, slug, kind);
                byCharacter[characterKey] = bucket;
            }

            bucket.Frames[stateStem] = path;
        }

        return byCharacter.Values
            .Select(BuildSet)
            .Where(set => set.Sequences.Count > 0)
            .OrderBy(set => set.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(set => set.Slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static CharacterAnimationSet? FindForAsset(IReadOnlyList<CharacterAnimationSet> sets, string relativeAssetPath)
    {
        string path = relativeAssetPath.Replace('\\', '/');
        if (!TryParseCombatPath(path, out string characterKey, out _, out _, out _))
            return null;

        return sets.FirstOrDefault(set =>
            string.Equals(set.CharacterKey, characterKey, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryParseCombatPath(
        string path,
        out string characterKey,
        out string slug,
        out string kind,
        out string stateStem)
    {
        characterKey = string.Empty;
        slug = string.Empty;
        kind = string.Empty;
        stateStem = string.Empty;

        string normalized = path.Replace('\\', '/').TrimStart('/');
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        // Asset index paths are often prefixed, e.g.
        // docs/Project/Art-Source/full-refresh/hero-combat/<slug>/walk-1.png
        // Locate the combat root segment rather than requiring it at parts[0].
        int combatIdx = -1;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals("hero-combat", StringComparison.OrdinalIgnoreCase)
                || parts[i].Equals("monster-combat", StringComparison.OrdinalIgnoreCase))
            {
                combatIdx = i;
                break;
            }
        }

        if (combatIdx < 0 || combatIdx + 1 >= parts.Length)
            return false;

        string root = parts[combatIdx];
        kind = root.Equals("hero-combat", StringComparison.OrdinalIgnoreCase) ? "hero" : "monster";
        int remaining = parts.Length - combatIdx;

        if (remaining == 2)
        {
            // Flat primary: .../hero-combat/human-fighter.png
            string file = parts[combatIdx + 1];
            if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return false;
            slug = Path.GetFileNameWithoutExtension(file);
            characterKey = $"{root}/{slug}";
            stateStem = "idle";
            return true;
        }

        if (remaining == 3)
        {
            // State-set: .../hero-combat/human-fighter/walk-1.png
            slug = parts[combatIdx + 1];
            string file = parts[combatIdx + 2];
            if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return false;
            characterKey = $"{root}/{slug}";
            stateStem = Path.GetFileNameWithoutExtension(file);
            return !string.IsNullOrWhiteSpace(stateStem);
        }

        return false;
    }

    private static CharacterAnimationSet BuildSet(CharacterBucket bucket)
    {
        var sequences = new List<AnimationSequence>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Walk cycle: any walk-N frames in numeric order (4- or 5-frame loops).
        List<string> walkStems = bucket.Frames.Keys
            .Where(k => k.StartsWith("walk-", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(k.AsSpan("walk-".Length), out _))
            .OrderBy(k => int.Parse(k.AsSpan("walk-".Length)))
            .ToList();
        List<string> walkPaths = walkStems.Select(f => bucket.Frames[f]).ToList();
        if (walkPaths.Count > 0)
        {
            sequences.Add(new AnimationSequence("walk", "Walk", LoopDefault: true, Fps: 8, Frames: walkPaths));
            foreach (string f in walkStems)
                used.Add(f);
        }

        // Shared single-frame combat states in stable order
        foreach (string state in SharedSequenceOrder)
        {
            if (state.Equals("walk", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!bucket.Frames.TryGetValue(state, out string? path))
                continue;
            string label = SharedLabels.GetValueOrDefault(state, Humanize(state));
            bool loop = state.Equals("idle", StringComparison.OrdinalIgnoreCase)
                || state.Equals("wait", StringComparison.OrdinalIgnoreCase)
                || state.Equals("defend", StringComparison.OrdinalIgnoreCase)
                || state.Equals("block", StringComparison.OrdinalIgnoreCase);
            sequences.Add(new AnimationSequence(state, label, LoopDefault: loop, Fps: 6, Frames: [path]));
            used.Add(state);
        }

        // Skill sequences (skill-* and optional numbered multi-frame skill-foo-1..)
        var skillGroups = new Dictionary<string, List<(int order, string path)>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string stem, string path) in bucket.Frames)
        {
            if (used.Contains(stem))
                continue;
            if (!stem.StartsWith("skill-", StringComparison.OrdinalIgnoreCase))
                continue;

            string group = stem;
            int order = 0;
            int lastDash = stem.LastIndexOf('-');
            if (lastDash > 6 && int.TryParse(stem[(lastDash + 1)..], out int n))
            {
                group = stem[..lastDash];
                order = n;
            }

            if (!skillGroups.TryGetValue(group, out List<(int order, string path)>? list))
            {
                list = [];
                skillGroups[group] = list;
            }

            list.Add((order, path));
            used.Add(stem);
        }

        foreach ((string group, List<(int order, string path)> frames) in skillGroups.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<string> paths = frames.OrderBy(f => f.order).Select(f => f.path).ToList();
            sequences.Add(new AnimationSequence(
                group,
                Humanize(group),
                LoopDefault: false,
                Fps: 6,
                Frames: paths));
        }

        // Any remaining state frames as one-shot sequences
        foreach ((string stem, string path) in bucket.Frames.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (used.Contains(stem))
                continue;
            sequences.Add(new AnimationSequence(stem, Humanize(stem), LoopDefault: false, Fps: 6, Frames: [path]));
        }

        string primaryPath = bucket.Frames.GetValueOrDefault("idle")
            ?? walkPaths.FirstOrDefault()
            ?? bucket.Frames.Values.First();

        return new CharacterAnimationSet(
            bucket.CharacterKey,
            bucket.Slug,
            bucket.Kind,
            primaryPath,
            sequences);
    }

    private static string Humanize(string slug)
    {
        string s = slug.Replace('-', ' ').Trim();
        if (s.StartsWith("skill ", StringComparison.OrdinalIgnoreCase))
            s = s["skill ".Length..];
        if (string.IsNullOrEmpty(s))
            return slug;
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private sealed class CharacterBucket(string characterKey, string slug, string kind)
    {
        public string CharacterKey { get; } = characterKey;
        public string Slug { get; } = slug;
        public string Kind { get; } = kind;
        public Dictionary<string, string> Frames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record CharacterAnimationSet(
    string CharacterKey,
    string Slug,
    string Kind,
    string PrimaryPath,
    IReadOnlyList<AnimationSequence> Sequences);

internal sealed record AnimationSequence(
    string Id,
    string Label,
    bool LoopDefault,
    int Fps,
    IReadOnlyList<string> Frames);
