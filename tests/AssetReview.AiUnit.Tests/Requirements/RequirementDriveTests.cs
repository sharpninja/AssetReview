namespace AssetReview.AiUnit.Tests;

public sealed class RequirementCatalogTests
{
    [Fact]
    public void EveryCapturedRequirement_IsLoadedDocumentedAndDriven()
    {
        var scenarios = RequirementCatalog.LoadAll();
        Assert.NotEmpty(scenarios);

        var markdown = File.ReadAllText(RequirementCatalog.MarkdownPath);
        foreach (var scenario in scenarios)
        {
            Assert.Matches("^(FR|TR)-ASSETREVIEW-\\d{3}$", scenario.Id);
            Assert.False(string.IsNullOrWhiteSpace(scenario.Title));
            Assert.False(string.IsNullOrWhiteSpace(scenario.Text));
            Assert.Contains(scenario.Id, markdown, StringComparison.Ordinal);
            Assert.Contains(scenario.Title, markdown, StringComparison.Ordinal);
            Assert.Contains(scenario.Text, markdown, StringComparison.Ordinal);
        }

        var expected = scenarios.Select(scenario => scenario.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var driven = RequirementDrivers.Ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, driven);
    }
}

[Collection(BrowserCollection.Name)]
public sealed class RequirementDriveTests
{
    private readonly PlaywrightBrowserFixture _browser;

    public RequirementDriveTests(PlaywrightBrowserFixture browser) => _browser = browser;

    [Theory]
    [MemberData(nameof(RequirementCatalog.RequirementIds), MemberType = typeof(RequirementCatalog))]
    public async Task Drive(string requirementId) =>
        await RequirementDrivers.RunAsync(requirementId, _browser);
}
