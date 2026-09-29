using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

namespace AssetReview.AiUnit.Tests;

internal static class ReviewUi
{
    public static ILocator Card(IPage page, string fileName) =>
        page.Locator($".asset-card:has(.asset-name:text-is({JsonSerializer.Serialize(fileName)}))");

    public static async Task OpenAsync(IPage page, string fileName)
    {
        await Card(page, fileName).ClickAsync();
        await Assertions.Expect(page.Locator("#detailView")).ToBeVisibleAsync();
        await page.WaitForFunctionAsync(
            "() => { const img = document.querySelector('#detailImage'); return !!img && img.complete && img.naturalWidth > 0; }");
    }

    public static async Task SetZoomAsync(IPage page, string value)
    {
        await page.Locator("#zoomSlider").EvaluateAsync(
            "(el, next) => { el.value = next; el.dispatchEvent(new Event('input', { bubbles: true })); }",
            value);
    }

    public static async Task CtrlWheelAsync(IPage page, double deltaY)
    {
        await page.Locator("#preview").EvaluateAsync(
            "(el, deltaY) => el.dispatchEvent(new WheelEvent('wheel', { deltaY, ctrlKey: true, bubbles: true, cancelable: true }))",
            deltaY);
    }

    public static async Task<double> ReadZoomAsync(IPage page)
    {
        var text = (await page.Locator("#zoomValue").InnerTextAsync()).Trim().TrimEnd('%');
        return double.Parse(text, CultureInfo.InvariantCulture) / 100d;
    }

    public static async Task<int> ImageWidthAsync(IPage page) =>
        await page.Locator("#detailImage").EvaluateAsync<int>("el => el.getBoundingClientRect().width");
}

internal static class ReviewApi
{
    public static async Task<IReadOnlyList<string>> AssetPathsAsync(string baseUrl)
    {
        using var client = new HttpClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync(new Uri(new Uri(baseUrl), "api/assets")));
        return doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(asset => asset.GetProperty("path").GetString() ?? string.Empty)
            .ToArray();
    }

    public static async Task<int> AnimationSetCountAsync(string baseUrl)
    {
        using var client = new HttpClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync(new Uri(new Uri(baseUrl), "api/animation-sets")));
        return doc.RootElement.GetArrayLength();
    }

    public static async Task<(int Status, string Body)> PostFeedbackAsync(string baseUrl, object body)
    {
        using var client = new HttpClient();
        using var response = await client.PostAsJsonAsync(new Uri(new Uri(baseUrl), "api/feedback"), body);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static async Task<(int Status, string? MediaType)> GetAssetAsync(string baseUrl, string relativePath)
    {
        using var client = new HttpClient();
        var escaped = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        using var response = await client.GetAsync(new Uri(new Uri(baseUrl), "assets/" + escaped));
        return ((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType);
    }
}
