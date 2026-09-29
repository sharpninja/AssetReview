# AssetReview

Standalone local asset and wireframe approval review tool for PNG and SVG folders.

Originally extracted from [BBCrawler](https://github.com/) (`AssetReview.Tool`) into its own repository so other projects can consume the `asset-review` .NET tool without depending on the BBCrawler solution.

## Tool

Package id: `SharpNinja.AssetReview.Tool`  
Command: `asset-review`  
Target framework: `net10.0` (Avalonia + ASP.NET Core WebView hybrid desktop host)

```powershell
asset-review
asset-review --asset-root artifacts/art-approval/full-refresh --title "Asset Review"
```

Feedback is written as JSON Lines to `.asset-review/feedback.jsonl` by default.
Each line records the asset path, decision, comment, preview context, and UTC timestamp.

The detail view previews assets over a selectable C64 palette background and supports
zoom through the slider or mouse wheel over the preview.

Use **Reload All** to rescan the asset root and refresh image URLs after regenerating PNG/SVG files.
Use **Clear Reviews** to remove non-approved review contents from the feedback file while keeping
the current approved decisions.

## Build

```powershell
dotnet build AssetReview.sln
dotnet pack src/AssetReview.Tool/AssetReview.Tool.csproj
```

Versioning is driven by GitVersion (`GitVersion.yml`, next-version `0.1.0`, ContinuousDeployment on `main`/`master`).
