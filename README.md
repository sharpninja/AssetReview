# AssetReview

Standalone local asset and wireframe approval review tool for PNG and SVG folders.

Originally extracted from BBCrawler so other projects can use the `asset-review` .NET tool independently.

## Tool

- Package ID `SharpNinja.AssetReview.Tool`
- Command `asset-review`
- Target framework `net10.0` (Avalonia and ASP.NET Core)

One `SharpNinja.AssetReview.Tool` package contains Windows (`win-x64`, `win-arm64`) and glibc-based Linux (`linux-x64`, `linux-arm64`) applications. The `asset-review` launcher detects the current OS and .NET process architecture, then starts the matching app in the caller's working directory. Install the .NET 10 and ASP.NET Core 10 runtimes for your platform.

```powershell
dotnet tool install --global SharpNinja.AssetReview.Tool
asset-review
asset-review --asset-root artifacts/art-approval/full-refresh --title "Asset Review"
```

The default opens the embedded desktop window. Use `--external-browser` to open the system browser, or `--server-only` to print the local URL without opening a window. See `asset-review --help` for the workspace, port, and feedback options.

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
asset-review --generate-manifest --asset-root path/to/assets
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

`{#id}` is an optional stable sequence ID. Without it, the app derives an ID from the name. In an ordered sequence, listing a path twice keeps a hold frame. Missing files are skipped, and an ordered sequence with no readable frames is left out of the UI. Headings inside HTML comments or fenced code blocks are ignored.

Frames listed only under a sequence are still part of the asset catalog, so single-asset review is unchanged. Opening a card reviews that asset and steps through the filtered asset list.

### Branches and loops in a manifest

Give each frame a `{#frame-id}` and indent its transitions beneath it. The first frame is the entry point. `Branch` adds a labeled choice, `Loop` adds a labeled return, and `Next` connects to the next frame explicitly. A frame with no outgoing transitions ends that path.

```markdown
### Sequence: Escape the gate {#escape}
- `boards/gate.png` {#gate}
  - Branch: Take the stairs -> stairs
  - Branch: Use the lift -> lift
- `boards/stairs.png` {#stairs}
  - Next: exit
- `boards/lift.png` {#lift}
  - Loop: Try the gate again -> gate
  - Next: exit
- `boards/exit.png` {#exit}
```

Frame IDs are case-insensitive within each storyboard. Each starts with a letter or digit and may contain letters, digits, hyphens, and underscores. Separate IDs can refer to the same image. Every frame must be reachable from the entry point through `Next` or `Branch`, and a `Loop` can return to its own frame or to a frame with a forward path to it. A frame can have one `Next` transition and any number of labeled transitions.

`--generate-manifest` preserves these definitions when it refreshes the asset list. The asset index reports invalid graphs with the manifest path and line number. Plain sequences of frame paths still use their listed order.

### Review mode

The toolbar **Review** control switches **Assets** and **Storyboards**.

**Assets** is single-asset review. The sidebar lists folders and the grid lists files. Previous/next and the arrow keys follow the filtered asset list. Combat animation playback still runs when you open a combat asset this way.

**Storyboards** lists sequences with their review status. A branched storyboard opens in **Activity diagram**, where PlantUML shows all frames, branch labels, and loop paths. Select **Simulate** to see the wireframe for each step in the previewer. Use the **Previous**, **Next**, and **Restart storyboard** icons to retrace your path, advance, or return to the first frame. Branch buttons show their manifest labels. Choose the labeled loop button to repeat, or select **Break** to take the loop's `Next` exit or see its labeled exit choices. A loop with no exit ends on its final wireframe. The filmstrip jumps to a frame. Linear storyboards open directly in the previewer. Approve and Request refinement apply to the current frame, and graph feedback records its frame ID. Esc or Back returns to the storyboard list.

### Local PlantUML renderer

AssetReview renders activity diagrams locally using a [PlantUML JAR](https://plantuml.com/download), Java, and [Graphviz](https://plantuml.com/graphviz-dot). Set `ASSET_REVIEW_PLANTUML_JAR` to the JAR path or place `plantuml.jar` in the `AssetReview` directory under your OS local application data directory. On Linux, that is typically `~/.local/share/AssetReview`. AssetReview finds Java through `JAVA_HOME`, a `jre` subdirectory in that same directory, or `PATH`. The storyboard view shows a message when rendering is unavailable and keeps frame review accessible.

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
| `Test` | Build, then run the unit and SharpNinja.aiUnit requirement tests |
| `PackAssetReviewTool` | Publish all four platform applications and verify the single launcher package under `artifacts/nuke/local-packages` |
| `DeployAssetReviewTool` | Pack, then uninstall-if-present and `dotnet tool install --global` from the local feed |
| `PublishToNuGet` | Pack, then `dotnet nuget push` to nuget.org (or `--nuget-source`) |

## Tests

See the [requirement baseline and test coverage](https://github.com/sharpninja/AssetReview/blob/main/docs/requirements/Asset-Review-Requirements.md) and the [tool guide](https://github.com/sharpninja/AssetReview/blob/main/src/AssetReview.Tool/README.md).

SharpNinja.aiUnit tests in `tests/AssetReview.AiUnit.Tests` launch `asset-review` and drive the review page. Scenario drivers write screenshots to `artifacts/aiunit/asset-review/<REQUIREMENT-ID>.png` (override with `ASSET_REVIEW_SCREENSHOT_DIR`). The storyboard tests also exercise manifest round-tripping, diagram rendering and recovery, and the wireframe shown at each simulator step.

```powershell
./build.ps1 Test
```

The suite uses headless Chromium (`ASSET_REVIEW_BROWSER` or `CHROME_PATH`, otherwise `google-chrome` on `PATH`). It does not need a display: UI tests pass `--server-only` and open the same page the embedded Avalonia host loads. The default `--embedded` window is not started here.

`[AiTheory]` screenshot audits call the configured frontier strategy and are skipped unless that strategy resolves (set an API key; see `appsettings.aiunit.json`) and `ASSET_REVIEW_VISUAL_AUDIT=true`.

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

GitVersion reads `GitVersion.yml`, where the current `next-version` is `0.2.1` and `main`/`master` use Continuous Deployment. Nuke's `CalculateVersion` target passes the resulting SemVer into pack, deploy, and publish.

```powershell
dotnet tool restore
dotnet tool run dotnet-gitversion
```

CI (`.github/workflows/pack.yml`) checks out full history, runs `PackAssetReviewTool`, and fails if the nupkg version is not GitVersion `SemVer`.
