using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AssetReview.AiUnit.Tests;

/// <summary>
/// Verifies the PlantUML storyboard and the associated frame review flow.
/// 2026-11-23: TEST-ASSETREVIEW-002 covers activity labels, graph traversal, and renderer recovery.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class StoryboardActivityTests(PlaywrightBrowserFixture browser)
{
    private const string Manifest = """
        ### Sequence: Escape {#escape}
        - `gate.png` {#gate}
          - Branch: Take the stairs -> stairs
          - Branch: Use the [lift] -> lift
        - `shared.png` {#stairs}
          - Next: exit
        - `shared.png` {#lift}
          - Loop: Try the gate again -> gate
          - Next: exit
        - `exit.png` {#exit}

        ### Sequence: Broken {#broken}
        - `gate.png` {#gate}
          - Next: missing

        ### Sequence: Linear {#linear}
        - `gate.png`
        - `exit.png`
        """;

    private const string SimulationManifest = """
        ### Sequence: Escape {#escape}
        - `gate.png` {#gate}
          - Branch: Take the stairs -> stairs
          - Branch: Use the [lift] -> lift
        - `stairs.png` {#stairs}
          - Next: exit
        - `lift.png` {#lift}
          - Loop: Try the gate again -> gate
          - Next: exit
        - `exit.png` {#exit}

        ### Sequence: Labeled exits {#labeled-exits}
        - `gate.png` {#gate}
          - Next: lift
        - `lift.png` {#lift}
          - Loop: Try the gate again -> gate
          - Branch: Take the stairs -> stairs
          - Branch: Leave now -> exit
        - `stairs.png` {#stairs}
        - `exit.png` {#exit}

        ### Sequence: Closed loop {#closed-loop}
        - `gate.png` {#gate}
          - Next: lift
        - `lift.png` {#lift}
          - Loop: Try the gate again -> gate
        """;

    /// <summary>
    /// Proves the simulation assertions recognize the displayed image, labels, and enabled navigation.
    /// 2026-11-23: TEST-ASSETREVIEW-004, AC-step-previews, AC-branch-buttons, and AC-loop-break.
    /// </summary>
    [Fact]
    public async Task Simulator_assertions_detect_an_incorrect_preview_or_controls()
    {
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.RouteAsync("https://storyboard.test/**", route => route.FulfillAsync(new()
        {
            ContentType = "image/svg+xml",
            Body = """<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"/>""",
        }));
        await page.SetContentAsync("""
            <div id="preview"><img id="detailImage" src="https://storyboard.test/assets/lift.png?v=1"></div>
            <button id="prevBtn"></button><button id="nextBtn" disabled></button>
            <button id="storyboardRestartBtn"></button><button id="storyboardBreakBtn">Break</button>
            <div id="storyboardTransitions"><button data-kind="branch">Use the [lift]</button></div>
            """);

        var expected = new SimulatorStep("lift.png", ["Use the [lift]"], Previous: true, Next: false, Break: true);
        var actual = await ReadSimulatorStepAsync(page);
        AssertSimulatorStep(expected, actual);
        foreach (var wrong in new[]
        {
            actual with { Wireframe = "gate.png" }, actual with { PreviewVisible = false },
            actual with { Branches = ["lift"] }, actual with { Previous = false },
            actual with { Next = true }, actual with { RestartVisible = false }, actual with { Break = false },
        })
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertSimulatorStep(expected, wrong));
    }

    /// <summary>
    /// Runs the simulator against distinct browser-loaded fixture wireframes and authored exits.
    /// 2026-11-23: TEST-ASSETREVIEW-004, AC-step-previews, AC-branch-buttons, and AC-loop-break.
    /// </summary>
    [Fact]
    public async Task Simulator_previews_each_step_and_honors_branches_and_loop_exits()
    {
        using var workspace = AssetWorkspace.Create();
        string[] names = ["gate.png", "stairs.png", "lift.png", "exit.png"];
        for (var i = 0; i < names.Length; i++)
            PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, names[i]), 16 + i, 16, new Rgb(40, 165, 125));
        File.WriteAllText(Path.Combine(workspace.AssetRoot, "_manifest.md"), SimulationManifest);

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Storyboard simulator", TestContext.Current.CancellationToken);
        var page = session.Page;
        var backward = page.GetByRole(AriaRole.Button, new() { Name = "Previous visited frame" });
        var forward = page.GetByRole(AriaRole.Button, new() { Name = "Next storyboard step" });
        var restart = page.GetByRole(AriaRole.Button, new() { Name = "Restart storyboard" });
        var leave = page.GetByRole(AriaRole.Button, new() { Name = "Break", Exact = true });
        var repeat = page.GetByRole(AriaRole.Button, new() { Name = "Try the gate again", Exact = true });
        var lift = page.GetByRole(AriaRole.Button, new() { Name = "Use the [lift]", Exact = true });
        var stairs = page.GetByRole(AriaRole.Button, new() { Name = "Take the stairs", Exact = true });
        string[] gateChoices = ["Take the stairs", "Use the [lift]"];

        async Task ExpectStep(string image, string[] branches, bool previous = true, bool next = false, bool canBreak = false)
            => AssertSimulatorStep(new SimulatorStep(image, branches, previous, next, canBreak), await ReadSimulatorStepAsync(page));

        async Task StartStoryboard(string name)
        {
            await page.Locator("#sequenceCatalog .asset-card").GetByText(name, new() { Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Simulate", Exact = true }).ClickAsync();
        }

        await page.Locator("[data-review-mode='storyboards']").ClickAsync();
        await StartStoryboard("Escape");
        await ExpectStep("gate.png", gateChoices, previous: false);
        await stairs.ClickAsync();
        await ExpectStep("stairs.png", [], next: true);
        await forward.ClickAsync();
        await ExpectStep("exit.png", []);
        await backward.ClickAsync();
        await ExpectStep("stairs.png", [], next: true);
        await restart.ClickAsync();
        await ExpectStep("gate.png", gateChoices, previous: false);
        await lift.ClickAsync();
        await ExpectStep("lift.png", [], next: true, canBreak: true);
        await repeat.ClickAsync();
        await ExpectStep("gate.png", gateChoices, canBreak: true);
        await leave.ClickAsync();
        await ExpectStep("exit.png", []);
        await backward.ClickAsync();
        await ExpectStep("gate.png", gateChoices, canBreak: true);
        await stairs.ClickAsync();
        await ExpectStep("stairs.png", [], next: true);

        await page.Locator("#backBtn").ClickAsync();
        await StartStoryboard("Labeled exits");
        await ExpectStep("gate.png", [], previous: false, next: true);
        await forward.ClickAsync();
        await ExpectStep("lift.png", ["Take the stairs", "Leave now"], canBreak: true);
        await leave.ClickAsync();
        await ExpectStep("lift.png", ["Take the stairs", "Leave now"]);
        await Expect(repeat).ToBeHiddenAsync();
        await stairs.ClickAsync();
        await ExpectStep("stairs.png", []);

        await page.Locator("#backBtn").ClickAsync();
        await StartStoryboard("Closed loop");
        await forward.ClickAsync();
        await ExpectStep("lift.png", [], canBreak: true);
        await repeat.ClickAsync();
        await ExpectStep("gate.png", [], next: true, canBreak: true);
        await leave.ClickAsync();
        await ExpectStep("lift.png", []);
        await Expect(repeat).ToBeHiddenAsync();
        await Expect(page.GetByText("Loop ended here.", new() { Exact = true })).ToBeVisibleAsync();
    }

    /// <summary>Reads the preview image only after the browser loads the currently selected asset.</summary>
    private static async Task<SimulatorSnapshot> ReadSimulatorStepAsync(IPage page)
    {
        await page.WaitForFunctionAsync("""
            () => {
              const image = document.getElementById('detailImage');
              return image?.complete && image.naturalWidth > 0 && image.currentSrc === image.src;
            }
            """);
        var snapshot = await page.EvaluateAsync<string>("""
            () => {
              const visible = id => document.getElementById(id)?.getClientRects().length > 0;
              const path = new URL(document.getElementById('detailImage').currentSrc).pathname;
              return JSON.stringify({
                wireframe: path.split('/').at(-1),
                branches: [...document.querySelectorAll('#storyboardTransitions button[data-kind="branch"]')].map(button => button.textContent),
                previewVisible: visible('preview'),
                previous: !document.getElementById('prevBtn').disabled,
                next: !document.getElementById('nextBtn').disabled,
                restartVisible: visible('storyboardRestartBtn'),
                break: visible('storyboardBreakBtn')
              });
            }
            """);
        return JsonSerializer.Deserialize<SimulatorSnapshot>(snapshot) ?? throw new InvalidOperationException("Missing simulator snapshot.");
    }

    private static void AssertSimulatorStep(SimulatorStep expected, SimulatorSnapshot actual)
    {
        Assert.True(actual.PreviewVisible);
        Assert.Equal(expected.Wireframe, actual.Wireframe);
        Assert.Equal(expected.Branches, actual.Branches);
        Assert.Equal(expected.Previous, actual.Previous);
        Assert.Equal(expected.Next, actual.Next);
        Assert.True(actual.RestartVisible);
        Assert.Equal(expected.Break, actual.Break);
    }

    private sealed record SimulatorStep(string Wireframe, string[] Branches, bool Previous, bool Next, bool Break);

    private sealed record SimulatorSnapshot(
        [property: JsonPropertyName("wireframe")] string Wireframe,
        [property: JsonPropertyName("branches")] string[] Branches,
        [property: JsonPropertyName("previewVisible")] bool PreviewVisible,
        [property: JsonPropertyName("previous")] bool Previous,
        [property: JsonPropertyName("next")] bool Next,
        [property: JsonPropertyName("restartVisible")] bool RestartVisible,
        [property: JsonPropertyName("break")] bool Break);

    /// <summary>Checks that the SVG assertions reject malformed output and missing labels.</summary>
    [Fact]
    public void Activity_diagram_assertions_detect_incomplete_output()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg"><path d="M1,1 L2,2"/><text>gate</text><text>stairs</text>
            <text>lift</text><text>exit</text><text>Take the stairs</text><text>Use the [lift]</text><text>Try the gate again</text></svg>
            """;
        AssertActivitySvg(svg);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertActivitySvg(svg.Replace("Use the [lift]", "Unknown", StringComparison.Ordinal)));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertActivitySvg(svg.Replace("<path d=\"M1,1 L2,2\"/>", string.Empty, StringComparison.Ordinal)));
    }

    /// <summary>Verifies real rendering when configured, traversal, and recoverable diagram errors.</summary>
    [Fact]
    public async Task Activity_diagram_renders_and_storyboard_navigation_follows_authored_edges()
    {
        using var workspace = AssetWorkspace.Create();
        foreach (var name in new[] { "gate.png", "shared.png", "exit.png" })
            PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, name), 16, 16, new Rgb(40, 165, 125));
        File.WriteAllText(Path.Combine(workspace.AssetRoot, "_manifest.md"), Manifest);

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Storyboard activity review", TestContext.Current.CancellationToken);
        var page = session.Page;
        var diagramResponse = await page.APIRequest.GetAsync(session.Url + "api/storyboards/escape/diagram");
        if (diagramResponse.Ok)
        {
            AssertActivitySvg(await diagramResponse.TextAsync());
        }
        else
        {
            Assert.Equal(503, diagramResponse.Status);
            Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASSET_REVIEW_PLANTUML_JAR")), await diagramResponse.TextAsync());
        }

        await page.Locator("[data-review-mode='storyboards']").ClickAsync();
        await page.Locator("#sequenceCatalog .asset-card").GetByText("Escape", new() { Exact = true }).ClickAsync();
        if (diagramResponse.Ok)
            await Expect(page.Locator("#storyboardDiagramImage")).ToBeVisibleAsync(new() { Timeout = 20_000 });
        else
            await Expect(page.Locator("#storyboardDiagramStatus")).ToContainTextAsync("PlantUML");

        var choices = page.Locator("#storyboardTransitions");
        await Expect(page.Locator("#nextBtn")).ToBeDisabledAsync();
        await choices.GetByText("Use the [lift]", new() { Exact = true }).ClickAsync();
        await Expect(page.Locator("#preview")).ToBeVisibleAsync();
        await Expect(page.Locator("#sequencePanelMeta")).ToContainTextAsync("Frame 3 of 4");
        await page.Locator("#approveBtn").ClickAsync();
        await Expect(page.Locator("#toast")).ToContainTextAsync("Saved approved feedback.");
        Assert.Contains("frameId=lift", File.ReadAllText(workspace.FeedbackFile), StringComparison.Ordinal);

        await choices.GetByText("Try the gate again", new() { Exact = true }).ClickAsync();
        await Expect(page.Locator("#detailPath")).ToHaveTextAsync("gate.png");
        await page.Locator("#prevBtn").ClickAsync();
        await Expect(page.Locator("#sequencePanelMeta")).ToContainTextAsync("Frame 3 of 4");
        await page.Locator("#nextBtn").ClickAsync();
        await Expect(page.Locator("#detailPath")).ToHaveTextAsync("exit.png");
        await Expect(page.Locator("#nextBtn")).ToBeDisabledAsync();

        await page.Locator("#backBtn").ClickAsync();
        await page.Locator("#sequenceCatalog .asset-card").GetByText("Broken", new() { Exact = true }).ClickAsync();
        await Expect(page.Locator("#storyboardDiagnostics")).ToContainTextAsync("missing");
        Assert.Equal(422, (await page.APIRequest.GetAsync(session.Url + "api/storyboards/broken/diagram")).Status);

        await page.Locator("#backBtn").ClickAsync();
        await page.Locator("#sequenceCatalog .asset-card").GetByText("Linear", new() { Exact = true }).ClickAsync();
        await Expect(page.Locator("#preview")).ToBeVisibleAsync();
        await page.Locator("#nextBtn").ClickAsync();
        await Expect(page.Locator("#detailPath")).ToHaveTextAsync("exit.png");
        await page.Locator("#nextBtn").ClickAsync();
        await Expect(page.Locator("#detailPath")).ToHaveTextAsync("gate.png");

        var linearDiagram = await page.APIRequest.GetAsync(session.Url + "api/storyboards/linear/diagram");
        if (diagramResponse.Ok)
        {
            Assert.True(linearDiagram.Ok, await linearDiagram.TextAsync());
            Assert.Contains("exit.png", await linearDiagram.TextAsync(), StringComparison.Ordinal);
        }

        await page.RouteAsync("**/api/storyboards/linear/diagram", route => route.FulfillAsync(new()
        {
            Status = 503, ContentType = "text/plain", Body = "PlantUML is unavailable.",
        }));
        await page.Locator("#storyboardDiagramBtn").ClickAsync();
        await Expect(page.Locator("#storyboardDiagramStatus")).ToHaveTextAsync("PlantUML is unavailable.");
        await page.Locator("#storyboardPreviewBtn").ClickAsync();
        await Expect(page.Locator("#preview")).ToBeVisibleAsync();
    }

    private static void AssertActivitySvg(string svg)
    {
        XNamespace ns = "http://www.w3.org/2000/svg";
        var root = XDocument.Parse(svg).Root;
        Assert.NotNull(root);
        Assert.Equal(ns + "svg", root.Name);
        Assert.NotEmpty(root.Descendants(ns + "path"));
        var labels = string.Join('\n', root.Descendants(ns + "text").Select(text => text.Value));
        foreach (string label in new[] { "gate", "stairs", "lift", "exit", "Take the stairs", "Use the [lift]", "Try the gate again" })
            Assert.Contains(label, labels, StringComparison.Ordinal);
    }
}
