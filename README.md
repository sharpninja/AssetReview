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

### Storyboard sequences

A manifest can group assets into named, ordered storyboard sequences. Sequences are explicit. `--generate-manifest` does not infer them from filenames or folders. Re-running `--generate-manifest` refreshes the flat asset list and keeps sequence sections already in `_manifest.md`.

A sequence is an ATX heading (`##` through `######`) whose text starts with `Sequence:` or `Storyboard:`. Frame paths use the same backtick syntax as the asset list, in order, and are relative to the manifest file. The sequence runs until the next heading.

```markdown
## Storyboards

### Sequence: Opening cinematic

- `boards/opening/01.png`
- `boards/opening/02.png`
- `boards/opening/03.png`

### Storyboard: Boss intro {#boss-intro}

- `boards/boss/01.png`
- `boards/boss/02.svg`
```

`{#id}` is an optional stable id. Without it, the app derives an id from the name. Listing a path twice keeps a hold frame. Missing files are skipped, and a sequence with no readable frames is left out of the UI. Headings inside HTML comments or fenced code blocks are not sequences.

Frames listed only under a sequence are still part of the asset catalog, so single-asset review is unchanged. Opening a card reviews that asset and steps through the filtered asset list.

### Review mode

The toolbar **Review** control switches **Assets** and **Storyboards**.

**Assets** is single-asset review. The sidebar lists folders and the grid lists files. Previous/next and the arrow keys follow the filtered asset list. Combat animation playback still runs when you open a combat asset this way.

**Storyboards** lists sequences instead of individual files. The status filter keeps sequences that are still open, fully approved, or marked for refinement. Opening a sequence reviews that sequence as one unit: previous/next and the arrow keys stay on its frames, and the filmstrip jumps to a frame. Approve and Request refinement still apply to the current frame. Esc or Back returns to the storyboard list. Switching **Review** leaves the open detail view and shows the list for the mode you picked.

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