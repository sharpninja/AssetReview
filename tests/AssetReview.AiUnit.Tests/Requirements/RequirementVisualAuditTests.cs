using System.Text.Json;
using SharpNinja.AiUnit.Frontier;
using SharpNinja.AiUnit.Validation;
using SharpNinja.AiUnit.Xunit;

namespace AssetReview.AiUnit.Tests;

[Collection(BrowserCollection.Name)]
public sealed class RequirementVisualAuditTests
{
    private readonly PlaywrightBrowserFixture _browser;

    public RequirementVisualAuditTests(PlaywrightBrowserFixture browser) => _browser = browser;

    [AiTheory]
    [MemberData(nameof(RequirementCatalog.RequirementIds), MemberType = typeof(RequirementCatalog))]
    public async Task VisualAudit_ConfirmsCapturedScreenshot(string requirementId)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ASSET_REVIEW_VISUAL_AUDIT"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Set ASSET_REVIEW_VISUAL_AUDIT=true to send captured screenshots to the configured aiUnit strategy.");
        }

        AiSkip.IfNoStrategy();
        await RequirementDrivers.RunAsync(requirementId, _browser);

        var pngPath = Path.Combine(ScreenshotArchive.Root, requirementId + ".png");
        Assert.True(File.Exists(pngPath), $"Driving {requirementId} did not write {pngPath}.");

        var scenario = RequirementCatalog.LoadAll().Single(item => item.Id == requirementId);
        var response = await AiStrategyFixture.Default.Client!.SendAsync(new FrontierRequest(
            SystemPrompt: """
                You audit one screenshot of the Asset Review app against one requirement.
                Return only a JSON object with requirementId, passed, and evidence.
                Set passed to true only when the screenshot visibly supports the requirement.
                """,
            UserMessage: $"Requirement {scenario.Id} ({scenario.Title}): {scenario.Text}",
            Attachments:
            [
                new FrontierAttachment("image/png", requirementId + ".png", await File.ReadAllBytesAsync(pngPath)),
            ],
            RequireJsonOutput: true,
            Temperature: 0));

        if (response.Error is { } error)
        {
            if (string.Equals(error.ErrorCode, "auth", StringComparison.OrdinalIgnoreCase) || error.HttpStatus is 401 or 403)
                Assert.Skip(error.Message);

            Assert.Fail($"{error.ErrorCode}: {error.Message}");
        }

        using var document = JsonDocument.Parse(response.Text ?? string.Empty);
        AiUnitJsonAssertions.Required(document.RootElement, "requirementId", "passed", "evidence");
        Assert.Equal(requirementId, document.RootElement.GetProperty("requirementId").GetString());
        Assert.True(
            document.RootElement.GetProperty("passed").GetBoolean(),
            document.RootElement.GetProperty("evidence").GetString());
    }
}
