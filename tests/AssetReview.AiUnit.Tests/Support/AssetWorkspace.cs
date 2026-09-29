namespace AssetReview.AiUnit.Tests;

internal sealed class AssetWorkspace : IDisposable
{
    private AssetWorkspace(string root, string workspace, string assetRoot, string feedbackFile)
    {
        Root = root;
        Workspace = workspace;
        AssetRoot = assetRoot;
        FeedbackFile = feedbackFile;
    }

    public string Root { get; }

    public string Workspace { get; }

    public string AssetRoot { get; }

    public string FeedbackFile { get; }

    public static AssetWorkspace Create(bool assetRootIsWorkspace = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "asset-review-aiunit", Guid.NewGuid().ToString("n"));
        var workspace = Path.Combine(root, "workspace");
        var assetRoot = assetRootIsWorkspace ? workspace : Path.Combine(workspace, "assets");
        Directory.CreateDirectory(assetRoot);
        var feedback = Path.Combine(workspace, "out", "feedback.jsonl");
        return new AssetWorkspace(root, workspace, assetRoot, feedback);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // The server process may still be releasing a file handle.
        }
    }
}
