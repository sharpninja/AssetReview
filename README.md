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

### Generate a review manifest

Scan the current directory (or `--asset-root`) for supported PNG/SVG media and write `_manifest.md` in the format the app expects, then exit:

```powershell
asset-review --generate-manifest
asset-review --generate-manifest --asset-root path\to\assets
```

The generated file lists each asset as a backtick-wrapped relative path (e.g. `` `subdir/icon.png` ``) so subsequent `asset-review` runs prefer the manifest over a full directory scan.

## Build (Nuke)

```powershell
.\build.ps1                  # default: Compile
.\build.ps1 PackAssetReviewTool
.\build.ps1 DeployAssetReviewTool
.\build.ps1 PublishToNuGet
.\build.ps1 --help
```

On Unix: `./build.sh` with the same targets.

| Target | Purpose |
|--------|---------|
| `Compile` | Restore + build `AssetReview.sln` (default) |
| `PackAssetReviewTool` | Pack `SharpNinja.AssetReview.Tool` nupkg under `artifacts/nuke/local-packages` |
| `DeployAssetReviewTool` | Pack, then uninstall-if-present and `dotnet tool install --global` from the local feed |
| `PublishToNuGet` | Pack, then `dotnet nuget push` to nuget.org (or `--nuget-source`) |

### Publish to NuGet

Set an API key in the environment (never hardcode it):

- **`NUGET_API_KEY`** (preferred)
- `NUGET_AUTH_TOKEN` (fallback if `NUGET_API_KEY` is unset)

```powershell
$env:NUGET_API_KEY = "<your-nuget.org-key>"
.\build.ps1 PublishToNuGet
# optional alternate feed:
.\build.ps1 PublishToNuGet --nuget-source https://api.nuget.org/v3/index.json
```

Versioning is driven by GitVersion (`GitVersion.yml`, next-version `0.2.0`, ContinuousDeployment on `main`/`master`). Nuke `CalculateVersion` runs `dotnet-gitversion` and passes that SemVer into pack, deploy, and publish. On `main`, Continuous Deployment keeps the same SemVer until a git tag; the package version is that SemVer, not a value hardcoded in the project file.

```powershell
dotnet tool restore
dotnet tool run dotnet-gitversion
```

CI (`.github/workflows/pack.yml`) checks out full history, runs `PackAssetReviewTool`, and fails if the nupkg version is not GitVersion `SemVer`.