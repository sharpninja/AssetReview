using System.Text.Json;

namespace AssetReview.AiUnit.Tests;

/// <summary>
/// Verifies the manifest contract through the CLI and asset index.
/// 2026-11-23: TEST-ASSETREVIEW-001, AC-authored-transitions, AC-roundtrip-and-legacy,
/// and AC-diagnostics cover frame identity, regeneration, and invalid graph isolation.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class StoryboardGraphManifestTests(PlaywrightBrowserFixture browser)
{
    private const string AuthoredGraph = """
        - `gate.png` {#gate}
          - Branch: Take the stairs -> stairs
          - Branch: Use the lift -> lift
        - `shared.png` {#stairs}
          - Next: exit
        - `shared.png` {#lift}
          - Loop: Try the gate again -> gate
          - Next: exit
        - `exit.png` {#exit}
        """;

    private const string OtherStoryboards = """
        ### Sequence: Broken {#broken}
        - `gate.png` {#start}
          - Next: missing-id
        - `missing.png` {#missing-file}

        ### Sequence: Unmarked cycle {#cycle}
        - `gate.png` {#start}
          - Next: end
        - `exit.png` {#end}
          - Next: start

        ### Sequence: Invalid loop {#invalid-loop}
        - `gate.png` {#start}
          - Loop: Jump ahead -> end
          - Next: end
        - `exit.png` {#end}

        ### Sequence: Legacy {#legacy}
        - `shared.png`
        - `gate.png`
        - `shared.png`

        ### Sequence: Commented {#commented} <!-- hidden draft
        - `exit.png` {#hidden}
        -->
        - `gate.png` {#visible}
          - Loop: Replay -> visible
        """;

    private const string ExpectedIndex = """
        { "sequences": [
          { "id": "escape", "frames": ["gate.png","shared.png","shared.png","exit.png"], "graph": {
            "entryFrameId": "gate",
            "nodes": [{"id":"gate","path":"gate.png"},{"id":"stairs","path":"shared.png"},
                      {"id":"lift","path":"shared.png"},{"id":"exit","path":"exit.png"}],
            "transitions": [
              {"fromFrameId":"gate","toFrameId":"stairs","kind":"branch","label":"Take the stairs"},
              {"fromFrameId":"gate","toFrameId":"lift","kind":"branch","label":"Use the lift"},
              {"fromFrameId":"stairs","toFrameId":"exit","kind":"next","label":null},
              {"fromFrameId":"lift","toFrameId":"gate","kind":"loop","label":"Try the gate again"},
              {"fromFrameId":"lift","toFrameId":"exit","kind":"next","label":null}], "diagnostics": [] } },
          { "id": "broken", "graph": { "transitions": [], "diagnostics": [
            {"code":"unknown-target","line":14,"manifestPath":"fixture"},
            {"code":"missing-frame-file","line":15,"manifestPath":"fixture"}] } },
          { "id": "cycle", "graph": { "transitions": [], "diagnostics": [
            {"code":"forward-cycle","line":17,"manifestPath":"fixture"}] } },
          { "id": "invalid-loop", "graph": { "transitions": [], "diagnostics": [
            {"code":"invalid-loop-target","line":25,"manifestPath":"fixture"}] } },
          { "id": "legacy", "frames": ["shared.png","gate.png","shared.png"] },
          { "id": "commented", "frames": ["gate.png"], "graph": { "entryFrameId":"visible", "diagnostics": [] } }
        ] }
        """;

    /// <summary>Proves the contract assertions against independent fixtures and negative controls.</summary>
    [Fact]
    public void Manifest_assertions_detect_incorrect_graphs_and_regeneration()
    {
        var manifest = CreateManifest();
        AssertIndex(ExpectedIndex, "fixture", manifest);
        AssertRegenerated(manifest);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertIndex(ExpectedIndex.Replace("Use the lift", "Wrong label", StringComparison.Ordinal), "fixture", manifest));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertIndex(ExpectedIndex.Replace("\"toFrameId\":\"gate\",\"kind\":\"loop\"", "\"toFrameId\":\"exit\",\"kind\":\"loop\"", StringComparison.Ordinal), "fixture", manifest));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertIndex(ExpectedIndex.Replace("\"shared.png\",\"gate.png\",\"shared.png\"", "\"gate.png\",\"shared.png\"", StringComparison.Ordinal), "fixture", manifest));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertIndex(ExpectedIndex.Replace("\"code\":\"unknown-target\",\"line\":14", "\"code\":\"unknown-target\",\"line\":1", StringComparison.Ordinal), "fixture", manifest));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertRegenerated(manifest.Replace("- Loop: Try the gate again -> gate", string.Empty, StringComparison.Ordinal)));
    }

    /// <summary>Exercises the same assertions against authored and regenerated manifests.</summary>
    [Fact]
    public async Task Load_and_regenerate_preserves_the_storyboard_graph()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = AssetWorkspace.Create();
        var manifestPath = Path.Combine(workspace.AssetRoot, "_manifest.md");
        foreach (var asset in new[] { "gate.png", "shared.png", "exit.png" })
            File.WriteAllBytes(Path.Combine(workspace.AssetRoot, asset), []);

        File.WriteAllText(manifestPath, CreateManifest());
        await using (var original = await ReviewSession.StartAsync(browser, workspace, "Storyboard graphs", cancellationToken))
            await AssertLiveIndexAsync(original, manifestPath);

        for (var pass = 0; pass < 2; pass++)
        {
            var result = await ToolProcess.RunAsync(
                ["--workspace", workspace.Workspace, "--asset-root", workspace.AssetRoot, "--generate-manifest"],
                cancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardError);
            AssertRegenerated(File.ReadAllText(manifestPath));
        }

        await using var regenerated = await ReviewSession.StartAsync(browser, workspace, "Storyboard graphs", cancellationToken);
        await AssertLiveIndexAsync(regenerated, manifestPath);
    }

    private static string CreateManifest() =>
        $"### Sequence: Escape {{#escape}}\n{AuthoredGraph}\n\n{OtherStoryboards}\n";

    private static async Task AssertLiveIndexAsync(ReviewSession session, string manifestPath)
    {
        var response = await session.Page.APIRequest.GetAsync(new Uri(new Uri(session.Url), "/api/assets").ToString());
        Assert.True(response.Ok);
        AssertIndex(await response.TextAsync(), manifestPath, File.ReadAllText(manifestPath));
    }

    private static void AssertRegenerated(string manifest)
    {
        var normalized = manifest.ReplaceLineEndings("\n");
        Assert.Contains(AuthoredGraph.ReplaceLineEndings("\n"), normalized, StringComparison.Ordinal);
        Assert.Contains("- Next: missing-id", normalized, StringComparison.Ordinal);
        Assert.Contains("### Sequence: Commented {#commented} <!-- hidden draft\n- `exit.png` {#hidden}\n-->", normalized, StringComparison.Ordinal);
        Assert.Single(normalized.Split('\n'), line => line == "### Sequence: Escape {#escape}");
    }

    private static void AssertIndex(string json, string manifestPath, string manifest)
    {
        using var document = JsonDocument.Parse(json);
        var sequences = document.RootElement.GetProperty("sequences").EnumerateArray()
            .ToDictionary(sequence => sequence.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var escape = sequences["escape"];
        var graph = escape.GetProperty("graph");

        Assert.Equal("gate", graph.GetProperty("entryFrameId").GetString());
        Assert.Equal(["gate.png", "shared.png", "shared.png", "exit.png"], FramePaths(escape));
        Assert.Equal(["gate=gate.png", "stairs=shared.png", "lift=shared.png", "exit=exit.png"],
            graph.GetProperty("nodes").EnumerateArray().Select(node => $"{node.GetProperty("id").GetString()}={node.GetProperty("path").GetString()}").ToArray());
        Assert.Equal([
            ("gate", "stairs", "branch", "Take the stairs"),
            ("gate", "lift", "branch", "Use the lift"),
            ("stairs", "exit", "next", null),
            ("lift", "gate", "loop", "Try the gate again"),
            ("lift", "exit", "next", null),
        ], graph.GetProperty("transitions").EnumerateArray().Select(edge => (
            edge.GetProperty("fromFrameId").GetString(), edge.GetProperty("toFrameId").GetString(),
            edge.GetProperty("kind").GetString(), edge.GetProperty("label").GetString())).ToArray());
        Assert.Empty(graph.GetProperty("diagnostics").EnumerateArray());

        Assert.Equal(["shared.png", "gate.png", "shared.png"], FramePaths(sequences["legacy"]));
        Assert.False(sequences["legacy"].TryGetProperty("graph", out _));
        var commented = sequences["commented"];
        Assert.Equal(["gate.png"], FramePaths(commented));
        Assert.Equal("visible", commented.GetProperty("graph").GetProperty("entryFrameId").GetString());
        Assert.Empty(commented.GetProperty("graph").GetProperty("diagnostics").EnumerateArray());
        AssertIssue(sequences["broken"], "unknown-target", "- Next: missing-id");
        AssertIssue(sequences["broken"], "missing-frame-file", "- `missing.png` {#missing-file}");
        AssertIssue(sequences["cycle"], "forward-cycle", "### Sequence: Unmarked cycle {#cycle}");
        AssertIssue(sequences["invalid-loop"], "invalid-loop-target", "- Loop: Jump ahead -> end");

        void AssertIssue(JsonElement sequence, string code, string sourceText)
        {
            var invalidGraph = sequence.GetProperty("graph");
            Assert.Empty(invalidGraph.GetProperty("transitions").EnumerateArray());
            var issue = Assert.Single(invalidGraph.GetProperty("diagnostics").EnumerateArray(), item => item.GetProperty("code").GetString() == code);
            Assert.Equal(manifestPath, issue.GetProperty("manifestPath").GetString());
            var expectedLine = Array.FindIndex(manifest.ReplaceLineEndings("\n").Split('\n'), line => line.Trim() == sourceText) + 1;
            Assert.Equal(expectedLine, issue.GetProperty("line").GetInt32());
        }
    }

    private static string[] FramePaths(JsonElement sequence) =>
        sequence.GetProperty("frames").EnumerateArray().Select(frame => frame.GetString()!).ToArray();
}
