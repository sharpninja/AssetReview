using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace AssetReview.AiUnit.Tests;

internal static class ScreenshotArchive
{
    private static readonly object Gate = new();

    public static string Root
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("ASSET_REVIEW_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return Path.GetFullPath(overrideDir);

            return Path.Combine(RequirementCatalog.RepositoryRoot, "artifacts", "aiunit", "asset-review");
        }
    }

    public static async Task<string> SaveAsync(IPage page, string requirementId, string note)
    {
        Directory.CreateDirectory(Root);
        var pngPath = Path.Combine(Root, requirementId + ".png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = pngPath });
        WriteSidecar(requirementId, pngPath, note);
        return pngPath;
    }

    public static async Task<string> SaveHtmlAsync(
        PlaywrightBrowserFixture browser,
        string requirementId,
        string title,
        string body,
        string note)
    {
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(EvidenceHtml(requirementId, title, body));
        return await SaveAsync(page, requirementId, note);
    }

    private static void WriteSidecar(string requirementId, string pngPath, string note)
    {
        var sidecar = new
        {
            requirementId,
            note,
            screenshot = pngPath,
            capturedUtc = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true });
        lock (Gate)
        {
            File.WriteAllText(Path.Combine(Root, requirementId + ".json"), json);
        }
    }

    private static string EvidenceHtml(string requirementId, string title, string body) =>
        $$"""
        <!doctype html>
        <meta charset="utf-8" />
        <title>{{WebUtility.HtmlEncode(requirementId)}}</title>
        <style>
          html, body { margin: 0; background: #0d1117; color: #e6edf3; }
          main { box-sizing: border-box; height: 900px; padding: 28px 32px; font: 16px/1.45 ui-monospace, Consolas, monospace; }
          h1 { margin: 0 0 8px; font-size: 22px; }
          p { margin: 0 0 16px; color: #9f9f9f; }
          pre { white-space: pre-wrap; background: #161b22; border: 1px solid #30363d; border-radius: 8px; padding: 16px; }
        </style>
        <main>
          <h1>{{WebUtility.HtmlEncode(requirementId)}}</h1>
          <p>{{WebUtility.HtmlEncode(title)}}</p>
          <pre>{{WebUtility.HtmlEncode(body)}}</pre>
        </main>
        """;
}
