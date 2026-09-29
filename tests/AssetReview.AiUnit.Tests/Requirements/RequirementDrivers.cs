using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AssetReview.AiUnit.Tests;

internal static class RequirementDrivers
{
    private static readonly Dictionary<string, Func<PlaywrightBrowserFixture, Task>> Map = new(StringComparer.Ordinal)
    {
        ["FR-ASSETREVIEW-001"] = DrivePngAndSvgGrid,
        ["FR-ASSETREVIEW-002"] = DriveFeedbackRecord,
        ["FR-ASSETREVIEW-003"] = DriveC64Palette,
        ["FR-ASSETREVIEW-004"] = DriveZoomAndScroll,
        ["FR-ASSETREVIEW-005"] = DriveReloadAll,
        ["FR-ASSETREVIEW-006"] = DriveClearReviews,
        ["FR-ASSETREVIEW-007"] = DriveGenerateManifest,
        ["FR-ASSETREVIEW-008"] = DriveManifestPreference,
        ["FR-ASSETREVIEW-009"] = DriveSearchAndFilters,
        ["FR-ASSETREVIEW-010"] = DriveApproveAndRefine,
        ["FR-ASSETREVIEW-011"] = DriveStoryboard,
        ["FR-ASSETREVIEW-012"] = DriveVersion,
        ["FR-ASSETREVIEW-013"] = DriveServerOnly,
        ["FR-ASSETREVIEW-014"] = DrivePathEscape,
        ["FR-ASSETREVIEW-015"] = DriveTheme,
        ["FR-ASSETREVIEW-016"] = DriveFolderRail,
        ["FR-ASSETREVIEW-017"] = DriveNavigation,
        ["TR-ASSETREVIEW-001"] = DriveUnknownDecision,
        ["TR-ASSETREVIEW-002"] = DrivePortValidation,
        ["TR-ASSETREVIEW-003"] = DriveExcludedPaths,
    };

    public static IReadOnlyCollection<string> Ids => Map.Keys;

    public static Task RunAsync(string requirementId, PlaywrightBrowserFixture browser)
    {
        if (!Map.TryGetValue(requirementId, out var run))
            throw new InvalidOperationException($"No asset-review driver is registered for {requirementId}.");

        return run(browser);
    }

    private static async Task DrivePngAndSvgGrid(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WritePreview(Path.Combine(workspace.AssetRoot, "icon.png"), 128, 128, new Rgb(80, 180, 90));
        SvgImages.Write(Path.Combine(workspace.AssetRoot, "badge.svg"), "SVG", "#40318d");
        File.WriteAllText(Path.Combine(workspace.AssetRoot, "notes.txt"), "not an asset");

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Sprite Review", CancellationToken.None);
        await Assertions.Expect(session.Page.Locator("#title")).ToHaveTextAsync("Sprite Review");
        await Assertions.Expect(ReviewUi.Card(session.Page, "icon.png")).ToBeVisibleAsync();
        await Assertions.Expect(ReviewUi.Card(session.Page, "badge.svg")).ToBeVisibleAsync();
        await Assertions.Expect(session.Page.Locator(".asset-card")).ToHaveCountAsync(2);
        await Assertions.Expect(session.Page.Locator("#meta")).ToContainTextAsync("2 assets");

        var svg = await ReviewApi.GetAssetAsync(session.Url, "badge.svg");
        Assert.Equal((int)HttpStatusCode.OK, svg.Status);
        Assert.Equal("image/svg+xml", svg.MediaType);

        var paths = await ReviewApi.AssetPathsAsync(session.Url);
        Assert.Equal(["badge.svg", "icon.png"], paths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        await ScreenshotArchive.SaveAsync(session.Page, "FR-ASSETREVIEW-001", "Grid lists the PNG and SVG under the configured title.");
    }

    private static async Task DriveFeedbackRecord(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WritePreview(Path.Combine(workspace.AssetRoot, "hero.png"), 96, 96, new Rgb(200, 80, 40));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Feedback Review", CancellationToken.None);

        await ReviewUi.OpenAsync(session.Page, "hero.png");
        await session.Page.Locator(".swatch[aria-label='Cyan']").ClickAsync();
        await ReviewUi.SetZoomAsync(session.Page, "3");
        await session.Page.Locator("#comment").FillAsync("Tighten the outline");
        await session.Page.Locator("#approveBtn").ClickAsync();
        await Assertions.Expect(session.Page.Locator("#toast")).ToContainTextAsync("Saved approved");

        var line = File.ReadLines(workspace.FeedbackFile).Last(entry => !string.IsNullOrWhiteSpace(entry));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("hero.png", root.GetProperty("assetPath").GetString());
        Assert.Equal("approved", root.GetProperty("decision").GetString());
        Assert.Equal("Tighten the outline", root.GetProperty("comment").GetString());
        var context = root.GetProperty("context").GetString() ?? string.Empty;
        Assert.Contains("background=Cyan", context, StringComparison.Ordinal);
        Assert.Contains("zoom=300%", context, StringComparison.Ordinal);
        var timestamp = root.GetProperty("timestampUtc").GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.InRange(DateTimeOffset.UtcNow - timestamp, TimeSpan.Zero, TimeSpan.FromMinutes(5));

        await ScreenshotArchive.SaveAsync(session.Page, "FR-ASSETREVIEW-002", "Approved feedback was written to the JSONL file.");
    }

    private static async Task DriveC64Palette(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WritePreview(Path.Combine(workspace.AssetRoot, "sprite.png"), 96, 96, new Rgb(255, 255, 255));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Palette Review", CancellationToken.None);
        await ReviewUi.OpenAsync(session.Page, "sprite.png");

        Assert.Equal(16, await session.Page.Locator(".swatch").CountAsync());
        await session.Page.Locator(".swatch[aria-label='Cyan']").ClickAsync();
        var cyan = await session.Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--asset-bg').trim().toLowerCase()");
        Assert.Equal("#67b6bd", cyan);
        await Assertions.Expect(session.Page.Locator("#contextLabel")).ToContainTextAsync("C64 Cyan");
        await Assertions.Expect(session.Page.Locator(".swatch[aria-label='Cyan']")).ToHaveClassAsync(new Regex(@"\bactive\b"));

        await session.Page.Locator(".swatch[aria-label='Yellow']").ClickAsync();
        await Assertions.Expect(session.Page.Locator("#contextLabel")).ToContainTextAsync("C64 Yellow");
        await ScreenshotArchive.SaveAsync(session.Page, "FR-ASSETREVIEW-003", "Detail preview on the Yellow C64 swatch.");
    }

    private static async Task DriveZoomAndScroll(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WritePreview(Path.Combine(workspace.AssetRoot, "wide.png"), 480, 360, new Rgb(40, 90, 200));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Zoom Review", CancellationToken.None);
        var page = session.Page;
        await ReviewUi.OpenAsync(page, "wide.png");

        var natural = await page.Locator("#detailImage").EvaluateAsync<int>("el => el.naturalWidth");
        Assert.Equal(480, natural);
        Assert.Equal(2, await ReviewUi.ReadZoomAsync(page));
        Assert.Equal(natural * 2, await StyledWidthAsync(page));

        await page.Locator("#zoomSlider").FocusAsync();
        await page.Locator("#zoomSlider").PressAsync("ArrowRight");
        Assert.Equal(2.25, await ReviewUi.ReadZoomAsync(page));

        await ReviewUi.SetZoomAsync(page, "4");
        Assert.Equal(4, await ReviewUi.ReadZoomAsync(page));
        Assert.Equal(natural * 4, await StyledWidthAsync(page));

        await ReviewUi.CtrlWheelAsync(page, -120);
        Assert.Equal(4.25, await ReviewUi.ReadZoomAsync(page));

        await ReviewUi.SetZoomAsync(page, "8");
        Assert.Equal(8, await ReviewUi.ReadZoomAsync(page));
        var preview = page.Locator("#preview");
        var beforeJson = await PreviewMetricsAsync(preview);
        using (var beforeDoc = JsonDocument.Parse(beforeJson))
        {
            var overflow = beforeDoc.RootElement.GetProperty("scrollHeight").GetDouble()
                - beforeDoc.RootElement.GetProperty("clientHeight").GetDouble();
            Assert.True(overflow > 1, $"Zoomed image did not overflow the preview. {beforeJson}");
        }

        var box = await preview.BoundingBoxAsync();
        Assert.NotNull(box);
        await page.Mouse.MoveAsync(box.X + (box.Width / 2), Math.Min(box.Y + 80, box.Y + box.Height - 8));
        var zoomBeforeWheel = await ReviewUi.ReadZoomAsync(page);
        await page.Mouse.WheelAsync(0, 800);
        try
        {
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#preview').scrollTop > 100",
                null,
                new PageWaitForFunctionOptions { Timeout = 2000 });
        }
        catch (TimeoutException)
        {
            var after = await PreviewMetricsAsync(preview);
            Assert.Fail($"Plain wheel did not scroll the preview. before={beforeJson} after={after}");
        }
        Assert.Equal(zoomBeforeWheel, await ReviewUi.ReadZoomAsync(page));

        await ScreenshotArchive.SaveAsync(page, "FR-ASSETREVIEW-004", "Preview zoomed to 800% and scrolled with the wheel.");
    }

    private static async Task DriveReloadAll(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "first.png"), 64, 64, new Rgb(20, 20, 20));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Reload Review", CancellationToken.None);
        await Assertions.Expect(session.Page.Locator(".asset-card")).ToHaveCountAsync(1);

        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "second.png"), 64, 64, new Rgb(180, 40, 40));
        await session.Page.Locator("#reloadBtn").ClickAsync();
        await Assertions.Expect(session.Page.Locator("#toast")).ToContainTextAsync("Reloaded 2 assets");
        await Assertions.Expect(ReviewUi.Card(session.Page, "second.png")).ToBeVisibleAsync();
        await Assertions.Expect(session.Page.Locator(".asset-card")).ToHaveCountAsync(2);
        await ScreenshotArchive.SaveAsync(session.Page, "FR-ASSETREVIEW-005", "Reload All picked up second.png.");
    }

    private static async Task DriveClearReviews(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "keep.png"), 64, 64, new Rgb(20, 140, 60));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "drop.png"), 64, 64, new Rgb(180, 40, 40));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Clear Review", CancellationToken.None);
        var page = session.Page;

        await ReviewUi.OpenAsync(page, "keep.png");
        await page.Locator("#comment").FillAsync("keep me");
        await page.Locator("#approveBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#toast")).ToContainTextAsync("Saved approved");
        await page.Locator("#backBtn").ClickAsync();

        await ReviewUi.OpenAsync(page, "drop.png");
        await page.Locator("#comment").FillAsync("redo the boot");
        await page.Locator("#refineBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#toast")).ToContainTextAsync("Saved refinement");

        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await page.Locator("#clearReviewsBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#toast")).ToContainTextAsync("Kept 1 approvals");
        await Assertions.Expect(page.Locator("#detailStatus")).ToHaveTextAsync("Open");

        var decisions = new List<(string? Path, string? Decision)>();
        foreach (var line in File.ReadLines(workspace.FeedbackFile).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            using var parsed = JsonDocument.Parse(line);
            decisions.Add((parsed.RootElement.GetProperty("assetPath").GetString(), parsed.RootElement.GetProperty("decision").GetString()));
        }

        Assert.Equal([("keep.png", "approved")], decisions);
        await Assertions.Expect(ReviewUi.Card(page, "keep.png").Locator(".status")).ToHaveTextAsync("approved");
        await ScreenshotArchive.SaveAsync(page, "FR-ASSETREVIEW-006", "Clear Reviews kept the approval and cleared the refinement.");
    }

    private static async Task DriveGenerateManifest(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "included.png"), 64, 64, new Rgb(10, 80, 160));
        SvgImages.Write(Path.Combine(workspace.AssetRoot, "nested", "mark.svg"), "SVG", "#883932");
        File.WriteAllText(Path.Combine(workspace.AssetRoot, "notes.txt"), "skip");
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "debug", "hidden.png"), 8, 8, new Rgb(0, 0, 0));

        var result = await ToolProcess.RunAsync(
            ["--generate-manifest", "--workspace", workspace.Workspace, "--asset-root", workspace.AssetRoot],
            CancellationToken.None,
            workspace.Workspace);
        Assert.Equal(0, result.ExitCode);
        var manifestPath = Path.Combine(workspace.AssetRoot, "_manifest.md");
        Assert.True(File.Exists(manifestPath), result.StandardOutput + result.StandardError);
        var manifest = File.ReadAllText(manifestPath);
        Assert.Contains("`included.png`", manifest, StringComparison.Ordinal);
        Assert.Contains("`nested/mark.svg`", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden.png", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", manifest, StringComparison.Ordinal);
        Assert.Contains("Wrote 2 asset(s)", result.StandardOutput, StringComparison.Ordinal);

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Manifest Review", CancellationToken.None);
        var paths = await ReviewApi.AssetPathsAsync(session.Url);
        Assert.Equal(["included.png", "nested/mark.svg"], paths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        await ScreenshotArchive.SaveAsync(session.Page, "FR-ASSETREVIEW-007", "Review grid after --generate-manifest.");
    }

    private static async Task DriveManifestPreference(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "kept.png"), 64, 64, new Rgb(20, 120, 80));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "omitted.png"), 64, 64, new Rgb(120, 20, 20));
        File.WriteAllText(
            Path.Combine(workspace.AssetRoot, "_manifest.md"),
            """
            # Asset Review Manifest

            - `kept.png`
            """);

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Manifest Preference", CancellationToken.None);
        var paths = await ReviewApi.AssetPathsAsync(session.Url);
        Assert.Equal(["kept.png"], paths);
        await Assertions.Expect(ReviewUi.Card(session.Page, "kept.png")).ToBeVisibleAsync();
        await Assertions.Expect(ReviewUi.Card(session.Page, "omitted.png")).ToHaveCountAsync(0);
        await Assertions.Expect(session.Page.Locator("#meta")).ToContainTextAsync("Manifest: 1 file");
        await ScreenshotArchive.SaveAsync(session.Page, "FR-ASSETREVIEW-008", "Index followed _manifest.md and hid omitted.png.");
    }

    private static async Task DriveSearchAndFilters(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        foreach (var name in new[] { "alpha.png", "beta.png", "gamma.png" })
            PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, name), 48, 48, new Rgb(30, 30, 30));

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Filter Review", CancellationToken.None);
        var page = session.Page;
        await ApproveAsync(page, "alpha.png");
        await RefineAsync(page, "beta.png");
        await page.Locator("#backBtn").ClickAsync();

        await page.Locator("[data-filter='approved']").ClickAsync();
        await Assertions.Expect(ReviewUi.Card(page, "alpha.png")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".asset-card")).ToHaveCountAsync(1);

        await page.Locator("[data-filter='refinement']").ClickAsync();
        await Assertions.Expect(ReviewUi.Card(page, "beta.png")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".asset-card")).ToHaveCountAsync(1);

        await page.Locator("[data-filter='open']").ClickAsync();
        await Assertions.Expect(ReviewUi.Card(page, "gamma.png")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".asset-card")).ToHaveCountAsync(1);

        await page.Locator("[data-filter='all']").ClickAsync();
        await page.Locator("#search").FillAsync("beta.png");
        await Assertions.Expect(ReviewUi.Card(page, "beta.png")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".asset-card")).ToHaveCountAsync(1);
        await ScreenshotArchive.SaveAsync(page, "FR-ASSETREVIEW-009", "Search limited the grid to beta.png.");
    }

    private static async Task DriveApproveAndRefine(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "ship.png"), 64, 64, new Rgb(20, 140, 60));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "tweak.png"), 64, 64, new Rgb(180, 120, 20));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Decision Review", CancellationToken.None);
        var page = session.Page;

        await ReviewUi.OpenAsync(page, "ship.png");
        await page.Locator("#approveBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#detailStatus")).ToHaveTextAsync("approved");
        await Assertions.Expect(page.Locator("#toast")).ToContainTextAsync("Saved approved");
        await page.Locator("#backBtn").ClickAsync();

        await ReviewUi.OpenAsync(page, "tweak.png");
        await page.Locator("#refineBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#detailStatus")).ToHaveTextAsync("refinement");
        await Assertions.Expect(page.Locator("#toast")).ToContainTextAsync("Saved refinement");
        await ScreenshotArchive.SaveAsync(page, "FR-ASSETREVIEW-010", "Request refinement updated the open asset.");
    }

    private static async Task DriveStoryboard(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        var hero = Path.Combine(workspace.AssetRoot, "hero-combat", "human-fighter");
        PngImages.WriteSolid(Path.Combine(hero, "idle.png"), 64, 64, new Rgb(80, 80, 80));
        PngImages.WriteSolid(Path.Combine(hero, "walk-1.png"), 64, 64, new Rgb(40, 80, 200));
        PngImages.WriteSolid(Path.Combine(hero, "walk-2.png"), 64, 64, new Rgb(40, 140, 200));
        PngImages.WriteSolid(Path.Combine(hero, "walk-3.png"), 64, 64, new Rgb(40, 200, 220));
        PngImages.WriteSolid(Path.Combine(hero, "basic-attack.png"), 64, 64, new Rgb(200, 40, 40));
        PngImages.WriteSolid(Path.Combine(hero, "skill-slash-1.png"), 64, 64, new Rgb(200, 160, 20));
        PngImages.WriteSolid(Path.Combine(hero, "skill-slash-2.png"), 64, 64, new Rgb(220, 200, 40));
        PngImages.WriteSolid(
            Path.Combine(workspace.AssetRoot, "monster-combat", "slime", "idle.png"),
            64,
            64,
            new Rgb(40, 160, 60));

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Storyboard Review", CancellationToken.None);
        var page = session.Page;
        Assert.Equal(2, await ReviewApi.AnimationSetCountAsync(session.Url));
        await Assertions.Expect(page.Locator("#meta")).ToContainTextAsync("Animation sets: 2");

        await ReviewUi.OpenAsync(page, "walk-1.png");
        await Assertions.Expect(page.Locator("#animPlayer")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#animKindBadge")).ToHaveTextAsync("hero");
        await Assertions.Expect(page.Locator("#animCharacterLabel")).ToHaveTextAsync("human-fighter");
        await Assertions.Expect(page.Locator("#animSeqButtons button.active")).ToHaveTextAsync("Walk");
        await Assertions.Expect(page.Locator("#animPlayBtn")).ToHaveTextAsync("Pause");
        await Assertions.Expect(page.Locator("#animLoop")).ToBeCheckedAsync();
        await Assertions.Expect(page.Locator("#animSeqButtons button:text-is('Slash')")).ToBeVisibleAsync();

        var firstFrame = await page.Locator("#animFrameMeta").InnerTextAsync();
        await page.WaitForFunctionAsync(
            "prev => { const el = document.getElementById('animFrameMeta'); return !!el && el.textContent !== prev; }",
            firstFrame);
        await ScreenshotArchive.SaveAsync(page, "FR-ASSETREVIEW-011", "Walk sequence playing in the storyboard.");

        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(page.Locator("#animPlayBtn")).ToHaveTextAsync("Play");
        await page.Locator("#animSeqButtons button:text-is('Attack')").ClickAsync();
        await Assertions.Expect(page.Locator("#detailImage")).ToHaveAttributeAsync("src", new Regex("basic-attack\\.png"));
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(ScreenshotArchive.Root, "FR-ASSETREVIEW-011-attack.png"),
        });
    }

    private static async Task DriveVersion(PlaywrightBrowserFixture browser)
    {
        var result = await ToolProcess.RunAsync(["--version"], CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("asset-review ", result.StandardOutput.Trim(), StringComparison.Ordinal);
        await ScreenshotArchive.SaveHtmlAsync(
            browser,
            "FR-ASSETREVIEW-012",
            "asset-review --version",
            result.StandardOutput.Trim(),
            "Version command output.");
    }

    private static async Task DriveServerOnly(PlaywrightBrowserFixture browser)
    {
        var help = await ToolProcess.RunAsync(["--help"], CancellationToken.None);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("--server-only", help.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--embedded", help.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--external-browser", help.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--generate-manifest", help.StandardOutput, StringComparison.Ordinal);

        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "solo.png"), 64, 64, new Rgb(90, 90, 160));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Server Only", CancellationToken.None);
        await session.WaitForOutputAsync("Launch mode: ServerOnly");
        Assert.Contains(session.Url.TrimEnd('/'), session.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", session.StandardError, StringComparison.OrdinalIgnoreCase);
        await Assertions.Expect(ReviewUi.Card(session.Page, "solo.png")).ToBeVisibleAsync();
        Assert.False(session.StandardOutput.Contains("Launch mode: Embedded", StringComparison.Ordinal));
        await ScreenshotArchive.SaveAsync(session.Page, "FR-ASSETREVIEW-013", "Page served by --server-only.");
    }

    private static async Task DrivePathEscape(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "ok.png"), 32, 32, new Rgb(0, 180, 0));
        PngImages.WriteSolid(Path.Combine(workspace.Workspace, "secret.png"), 32, 32, new Rgb(180, 0, 0));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Path Review", CancellationToken.None);

        var ok = await ReviewApi.GetAssetAsync(session.Url, "ok.png");
        Assert.Equal((int)HttpStatusCode.OK, ok.Status);
        Assert.Equal("image/png", ok.MediaType);

        using var client = new HttpClient();
        using var escaped = await client.GetAsync(new Uri(new Uri(session.Url), "assets/..%2Fsecret.png"));
        Assert.Equal(HttpStatusCode.NotFound, escaped.StatusCode);

        var feedback = await ReviewApi.PostFeedbackAsync(session.Url, new
        {
            assetPath = "../secret.png",
            decision = "approved",
            comment = "nope",
            context = "background=Black; zoom=200%",
        });
        Assert.Equal((int)HttpStatusCode.NotFound, feedback.Status);
        Assert.False(File.Exists(workspace.FeedbackFile) && File.ReadAllText(workspace.FeedbackFile).Contains("secret.png", StringComparison.Ordinal));
        await ScreenshotArchive.SaveAsync(session.Page, "FR-ASSETREVIEW-014", "Review stayed inside the asset root.");
    }

    private static async Task DriveTheme(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "theme.png"), 64, 64, new Rgb(200, 200, 200));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Theme Review", CancellationToken.None);
        var page = session.Page;

        await page.GotoAsync(session.Url + "?theme=dark", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-theme", "dark");
        await ScreenshotArchive.SaveAsync(page, "FR-ASSETREVIEW-015", "Dark theme from the theme query.");

        await page.EvaluateAsync("() => window.assetReviewSetTheme('light')");
        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-theme", "light");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(ScreenshotArchive.Root, "FR-ASSETREVIEW-015-light.png"),
        });
    }

    private static async Task DriveFolderRail(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "ui", "button.png"), 48, 48, new Rgb(40, 80, 160));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "icons", "sword.png"), 48, 48, new Rgb(160, 80, 40));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Folder Review", CancellationToken.None);
        var page = session.Page;

        await Assertions.Expect(page.Locator(".folder").Filter(new LocatorFilterOptions { HasText = "icons" })).ToContainTextAsync("0/0/1");
        await page.Locator(".folder").Filter(new LocatorFilterOptions { HasText = "icons" }).ClickAsync();
        await Assertions.Expect(ReviewUi.Card(page, "sword.png")).ToBeVisibleAsync();
        await Assertions.Expect(ReviewUi.Card(page, "button.png")).ToHaveCountAsync(0);
        await ScreenshotArchive.SaveAsync(page, "FR-ASSETREVIEW-016", "Folder rail filtered the grid to icons.");
    }

    private static async Task DriveNavigation(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        foreach (var name in new[] { "a.png", "b.png", "c.png" })
            PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, name), 48, 48, new Rgb(50, 50, 50));

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Navigate Review", CancellationToken.None);
        var page = session.Page;
        await ReviewUi.OpenAsync(page, "a.png");
        await Assertions.Expect(page.Locator("#assetCount")).ToHaveTextAsync("1 / 3");
        await page.Locator("#nextBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#detailName")).ToHaveTextAsync("b.png");
        await page.Locator("#nextBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#detailName")).ToHaveTextAsync("c.png");
        await page.Locator("#prevBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#detailName")).ToHaveTextAsync("b.png");
        await page.Locator("#backBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#detailView")).ToBeHiddenAsync();
        await Assertions.Expect(page.Locator("#gridView")).ToBeVisibleAsync();
        await ScreenshotArchive.SaveAsync(page, "FR-ASSETREVIEW-017", "Back returned to the asset grid.");
    }

    private static async Task DriveUnknownDecision(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create();
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "ok.png"), 32, 32, new Rgb(0, 100, 0));
        await using var session = await ReviewSession.StartAsync(browser, workspace, "Decision API", CancellationToken.None);

        var rejected = await ReviewApi.PostFeedbackAsync(session.Url, new
        {
            assetPath = "ok.png",
            decision = "rejected",
            comment = "no",
            context = "",
        });
        Assert.Equal((int)HttpStatusCode.BadRequest, rejected.Status);
        Assert.Contains("approved or refinement", rejected.Body, StringComparison.Ordinal);

        var approved = await ReviewApi.PostFeedbackAsync(session.Url, new
        {
            assetPath = "ok.png",
            decision = "approved",
            comment = "yes",
            context = "background=Black; zoom=200%",
        });
        Assert.Equal((int)HttpStatusCode.OK, approved.Status);
        await ScreenshotArchive.SaveAsync(session.Page, "TR-ASSETREVIEW-001", "Unknown decisions are rejected; approved still saves.");
    }

    private static async Task DrivePortValidation(PlaywrightBrowserFixture browser)
    {
        var zero = await ToolProcess.RunAsync(["--port", "0", "--server-only"], CancellationToken.None);
        Assert.Equal(2, zero.ExitCode);
        Assert.Contains("out of range", zero.StandardError, StringComparison.OrdinalIgnoreCase);

        var text = await ToolProcess.RunAsync(["--port", "nope", "--server-only"], CancellationToken.None);
        Assert.Equal(2, text.ExitCode);
        Assert.Contains("not a valid integer", text.StandardError, StringComparison.OrdinalIgnoreCase);

        await ScreenshotArchive.SaveHtmlAsync(
            browser,
            "TR-ASSETREVIEW-002",
            "asset-review --port validation",
            $"--port 0 exit {zero.ExitCode}\n{zero.StandardError}\n--port nope exit {text.ExitCode}\n{text.StandardError}",
            "Invalid port values exit 2.");
    }

    private static async Task DriveExcludedPaths(PlaywrightBrowserFixture browser)
    {
        using var workspace = AssetWorkspace.Create(assetRootIsWorkspace: true);
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "included.png"), 48, 48, new Rgb(20, 80, 140));
        SvgImages.Write(Path.Combine(workspace.AssetRoot, "nested", "mark.svg"), "SVG", "#574200");
        File.WriteAllText(Path.Combine(workspace.AssetRoot, "notes.txt"), "skip");
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "shot-adj.png"), 8, 8, new Rgb(1, 1, 1));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "debug", "hidden.png"), 8, 8, new Rgb(2, 2, 2));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "screenshots", "shot.png"), 8, 8, new Rgb(3, 3, 3));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "crawl-shots", "shot.png"), 8, 8, new Rgb(4, 4, 4));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "test-output", "shot.png"), 8, 8, new Rgb(5, 5, 5));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "artifacts", "noise.png"), 8, 8, new Rgb(6, 6, 6));
        PngImages.WriteSolid(Path.Combine(workspace.AssetRoot, "artifacts", "art-approval", "keep.png"), 48, 48, new Rgb(20, 140, 80));

        var generated = await ToolProcess.RunAsync(
            ["--generate-manifest", "--workspace", workspace.Workspace, "--asset-root", workspace.AssetRoot],
            CancellationToken.None,
            workspace.Workspace);
        Assert.Equal(0, generated.ExitCode);
        var manifest = File.ReadAllText(Path.Combine(workspace.AssetRoot, "_manifest.md"));
        Assert.Contains("`included.png`", manifest, StringComparison.Ordinal);
        Assert.Contains("`nested/mark.svg`", manifest, StringComparison.Ordinal);
        Assert.Contains("`artifacts/art-approval/keep.png`", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("shot-adj.png", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden.png", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("noise.png", manifest, StringComparison.Ordinal);

        await using var session = await ReviewSession.StartAsync(browser, workspace, "Exclusion Review", CancellationToken.None);
        var paths = await ReviewApi.AssetPathsAsync(session.Url);
        Assert.Equal(
            ["artifacts/art-approval/keep.png", "included.png", "nested/mark.svg"],
            paths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        await Assertions.Expect(ReviewUi.Card(session.Page, "included.png")).ToBeVisibleAsync();
        await Assertions.Expect(ReviewUi.Card(session.Page, "shot-adj.png")).ToHaveCountAsync(0);
        await ScreenshotArchive.SaveAsync(session.Page, "TR-ASSETREVIEW-003", "Excluded debug and screenshot paths stayed out of the grid.");
    }

    private static async Task<int> StyledWidthAsync(IPage page) =>
        await page.Locator("#detailImage").EvaluateAsync<int>("el => parseInt(el.style.width, 10)");

    private static async Task<string> PreviewMetricsAsync(ILocator preview) =>
        await preview.EvaluateAsync<string>(
            "el => JSON.stringify({ scrollTop: el.scrollTop, scrollLeft: el.scrollLeft, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight, scrollWidth: el.scrollWidth, clientWidth: el.clientWidth })");

    private static async Task ApproveAsync(IPage page, string fileName)
    {
        await ReviewUi.OpenAsync(page, fileName);
        await page.Locator("#approveBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#toast")).ToContainTextAsync("Saved approved");
        await page.Locator("#backBtn").ClickAsync();
    }

    private static async Task RefineAsync(IPage page, string fileName)
    {
        await ReviewUi.OpenAsync(page, fileName);
        await page.Locator("#refineBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#toast")).ToContainTextAsync("Saved refinement");
    }
}
