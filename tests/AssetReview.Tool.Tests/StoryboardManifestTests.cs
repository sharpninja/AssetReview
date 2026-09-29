using System.Text.Json;
using SharpNinja.AssetReview.Tool;
using Xunit;

namespace AssetReview.Tool.Tests;

public sealed class StoryboardManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asset-review-storyboards", Guid.NewGuid().ToString("n"));
    private readonly List<string> _cleanup = [];

    public StoryboardManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        foreach (string path in _cleanup)
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void Parse_reads_named_sequences_in_order_and_ignores_comments_and_fences()
    {
        IReadOnlyList<RawStoryboard> sequences = StoryboardManifest.Parse("""
            # Asset Review Manifest

            - `loose.png`

            <!--
            ## Sequence: Hidden
            - `hidden.png`
            -->

            ```
            ## Sequence: Fenced
            - `fenced.png`
            ```

            ## Sequence: Opening cinematic {#opening}
            - `c.png`
            - `a.png` `b.png`

            ## Notes
            - `notes.png`

            ### storyboard: Boss intro
            - `boss.svg`

            ## Sequence notes
            - `not-a-sequence.png`

            ## Sequence: Inline `ignored.png`
            - `after.png`
            """);

        Assert.Equal(3, sequences.Count);

        Assert.Equal("Opening cinematic", sequences[0].Name);
        Assert.Equal("opening", sequences[0].ExplicitId);
        Assert.Equal(["c.png", "a.png", "b.png"], sequences[0].FramePaths);

        Assert.Equal("Boss intro", sequences[1].Name);
        Assert.Null(sequences[1].ExplicitId);
        Assert.Equal(["boss.svg"], sequences[1].FramePaths);

        Assert.Equal("Inline `ignored.png`", sequences[2].Name);
        Assert.Equal(["after.png"], sequences[2].FramePaths);
    }

    [Fact]
    public void Parse_accepts_crlf_and_does_not_treat_the_generator_note_as_a_sequence()
    {
        IReadOnlyList<RawStoryboard> sequences = StoryboardManifest.Parse(
            "# Asset Review Manifest\r\n- `a.png`\r\n## Sequence: Dawn\r\n- `a.png`\r\n\r\n> Storyboard sequences are optional and are not inferred. Add a heading like \"### Sequence: Name\" and list ordered frame paths beneath it.\r\n");

        Assert.Single(sequences);
        Assert.Equal("Dawn", sequences[0].Name);
        Assert.Equal(["a.png"], sequences[0].FramePaths);
    }

    [Fact]
    public void Load_resolves_frames_against_the_manifest_directory_and_drops_unsafe_or_missing_paths()
    {
        Touch("pack/shot.png");
        Touch("pack/other.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        Touch("pack/my frames/frame 1.png");
        Touch("loose.png");
        string outside = Path.Combine(Directory.GetParent(_root)!.FullName, "outside-" + Guid.NewGuid().ToString("n") + ".png");
        File.WriteAllText(outside, "nope");
        _cleanup.Add(outside);

        string manifest = Path.Combine(_root, "pack", "_manifest.md");
        File.WriteAllText(manifest, """
            - `../loose.png`
            ### Sequence: Intro
            - `shot.png`
            - `shot.png`
            - `other.svg`
            - `missing.png`
            - `../../outside.png`
            - `notes.txt`
            - `my frames/frame 1.png`
            ## Sequence: Empty
            - `missing.png`
            """);

        AssetIndex index = Program.BuildIndex(Options(manifest));

        Assert.Equal(["loose.png", "pack/other.svg", "pack/shot.png", "pack/my frames/frame 1.png"], index.Assets.Select(asset => asset.Path).ToArray());
        StoryboardSequence sequence = Assert.Single(index.Sequences);
        Assert.Equal("intro", sequence.Id);
        Assert.Equal("Intro", sequence.Name);
        Assert.Equal(
            ["pack/shot.png", "pack/shot.png", "pack/other.svg", "pack/my frames/frame 1.png"],
            sequence.Frames);
    }

    [Fact]
    public void Load_assigns_unique_ids_and_keeps_single_asset_feedback()
    {
        Touch("a.png");
        Touch("b.png");
        string manifest = Path.Combine(_root, "_manifest.md");
        File.WriteAllText(manifest, """
            - `a.png`
            - `b.png`
            ## Sequence: Opening {#opening}
            - `a.png`
            ## Sequence: Opening
            - `b.png`
            """);
        Directory.CreateDirectory(Path.Combine(_root, ".asset-review"));
        string feedback = Path.Combine(_root, ".asset-review", "feedback.jsonl");
        File.WriteAllText(feedback, JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.Parse("2026-09-29T00:00:00Z"),
            assetPath = "a.png",
            decision = "approved",
            comment = "hold",
            context = "sequence=Opening",
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");

        AssetIndex index = Program.BuildIndex(Options(manifest));

        Assert.Equal(["opening", "opening-2"], index.Sequences.Select(sequence => sequence.Id).ToArray());
        Assert.Equal("approved", Assert.Single(index.Assets, asset => asset.Path == "a.png").Decision);
        Assert.Null(Assert.Single(index.Assets, asset => asset.Path == "b.png").Decision);

        string json = JsonSerializer.Serialize(index, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"sequences\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frames\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_concatenates_sequences_from_the_selected_manifests()
    {
        Touch("a/one.png");
        Touch("b/two.png");
        string first = Path.Combine(_root, "a", "_manifest.md");
        string second = Path.Combine(_root, "b", "_manifest.md");
        File.WriteAllText(first, """
            ## Sequence: Alpha
            - `one.png`
            """);
        File.WriteAllText(second, """
            ## Storyboard: Beta
            - `two.png`
            """);

        AssetIndex index = Program.BuildIndex(Options(second, first));

        Assert.Equal(["Alpha", "Beta"], index.Sequences.Select(sequence => sequence.Name).ToArray());
        Assert.Equal(["a/one.png"], index.Sequences[0].Frames);
        Assert.Equal(["b/two.png"], index.Sequences[1].Frames);
    }

    [Fact]
    public void GenerateManifest_does_not_infer_sequences_from_combat_frame_names()
    {
        Touch("hero-combat/hero/idle.png");
        Touch("hero-combat/hero/walk-1.png");
        Touch("hero-combat/hero/walk-2.png");

        Assert.Equal(0, Program.GenerateManifest(Options()));

        string text = File.ReadAllText(Path.Combine(_root, "_manifest.md"));
        Assert.Contains("- `hero-combat/hero/walk-1.png`", text, StringComparison.Ordinal);
        Assert.Contains("are not inferred", text, StringComparison.Ordinal);
        Assert.DoesNotContain(text.Split('\n'), line => line.TrimStart().StartsWith("### Sequence:", StringComparison.Ordinal));
        Assert.Empty(StoryboardManifest.Parse(text));
    }

    [Fact]
    public void GenerateManifest_preserves_authored_sequences_and_refreshes_the_asset_list()
    {
        Touch("a.png");
        Touch("b.png");
        Touch("c.png");
        File.WriteAllText(Path.Combine(_root, "_manifest.md"), """
            # Asset Review Manifest

            - `a.png`

            ## Sequence: Opening cinematic {#opening}

            - `c.png`
            - `a.png`
            - `b.png`

            ## Sequence: Stub
            """);
        Touch("d.png");
        File.Delete(Path.Combine(_root, "b.png"));

        Assert.Equal(0, Program.GenerateManifest(Options()));
        string text = File.ReadAllText(Path.Combine(_root, "_manifest.md"));
        IReadOnlyList<RawStoryboard> parsed = StoryboardManifest.Parse(text);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Opening cinematic", parsed[0].Name);
        Assert.Equal("opening", parsed[0].ExplicitId);
        Assert.Equal(["c.png", "a.png", "b.png"], parsed[0].FramePaths);
        Assert.Equal("Stub", parsed[1].Name);
        Assert.Empty(parsed[1].FramePaths);
        Assert.Contains("- `d.png`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- `b.png`", text.Split("## Storyboards")[0], StringComparison.Ordinal);
        Assert.Equal(1, CountOf(text, "## Storyboards"));

        Assert.Equal(0, Program.GenerateManifest(Options()));
        string second = File.ReadAllText(Path.Combine(_root, "_manifest.md"));
        IReadOnlyList<RawStoryboard> again = StoryboardManifest.Parse(second);
        Assert.Equal(parsed.Select(sequence => (sequence.Name, sequence.ExplicitId, Frames: string.Join("|", sequence.FramePaths))),
            again.Select(sequence => (sequence.Name, sequence.ExplicitId, Frames: string.Join("|", sequence.FramePaths))));
        Assert.Equal(1, CountOf(second, "## Storyboards"));
    }

    private ReviewOptions Options(params string[] manifests) =>
        new(_root, _root, Path.Combine(_root, ".asset-review", "feedback.jsonl"), 9, "Test", ReviewLaunchMode.ServerOnly)
        {
            ManifestFiles = manifests,
        };

    private void Touch(string relative, string contents = "img")
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
