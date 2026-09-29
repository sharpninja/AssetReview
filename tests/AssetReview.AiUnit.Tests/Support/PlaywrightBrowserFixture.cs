using Microsoft.Playwright;

namespace AssetReview.AiUnit.Tests;

[CollectionDefinition(BrowserCollection.Name)]
public sealed class BrowserCollection : ICollectionFixture<PlaywrightBrowserFixture>
{
    public const string Name = "AssetReviewBrowser";
}

public sealed class PlaywrightBrowserFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = FindChrome(),
            Args =
            [
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu",
            ],
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
            await Browser.CloseAsync();
        Playwright?.Dispose();
    }

    public async Task<IBrowserContext> NewContextAsync()
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1360, Height = 900 },
            DeviceScaleFactor = 1,
        });
        context.SetDefaultTimeout(15_000);
        context.SetDefaultNavigationTimeout(20_000);
        return context;
    }

    private static string FindChrome()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            "Headless Chromium was not found. Install Google Chrome or set ASSET_REVIEW_BROWSER (or CHROME_PATH) to the browser executable. A display is not required.");
    }

    private static IEnumerable<string> Candidates()
    {
        var configured = Environment.GetEnvironmentVariable("ASSET_REVIEW_BROWSER");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        var chromePath = Environment.GetEnvironmentVariable("CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(chromePath))
            yield return chromePath;

        yield return "/usr/local/bin/google-chrome";
        yield return "/usr/bin/google-chrome";
        yield return "/usr/bin/google-chrome-stable";
        yield return "/usr/bin/chromium";
        yield return "/usr/bin/chromium-browser";
        yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        yield return @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        yield return @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    }
}
