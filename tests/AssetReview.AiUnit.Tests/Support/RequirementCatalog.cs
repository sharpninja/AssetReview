using System.Text.Json;
using SharpNinja.AiUnit.Scenarios;

namespace AssetReview.AiUnit.Tests;

internal sealed class RequirementScenario
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;
}

internal static class RequirementCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<RequirementScenario> LoadAll() =>
        AiUnitScenarioCatalog.LoadAll(
            "scenarios",
            static (path, text) =>
            JsonSerializer.Deserialize<RequirementScenario>(text, JsonOptions)
            ?? throw new InvalidDataException($"Requirement scenario '{path}' was empty."),
        "*.json");

    public static IEnumerable<object[]> RequirementIds() =>
        LoadAll().Select(scenario => new object[] { scenario.Id });

    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AssetReview.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate AssetReview.sln from the test output directory.");
        }
    }

    public static string MarkdownPath =>
        Path.Combine(RepositoryRoot, "docs", "requirements", "Asset-Review-Requirements.md");
}
