# Asset Review Tool

> Extracted from BBCrawler into the standalone [AssetReview](https://github.com/sharpninja/AssetReview) repository. The package ID is `SharpNinja.AssetReview.Tool` and the command is `asset-review`.

Launches a local review app for PNG and SVG assets in the current workspace.

```powershell
asset-review
asset-review --asset-root artifacts/art-approval/full-refresh --title "BBCrawler Asset Review"
asset-review --generate-manifest
```

### Windows and Linux package

The `asset-review` launcher selects the Windows or glibc-based Linux app for the running .NET process's architecture. The package includes x64 and ARM64 builds for both platforms. Use the .NET 10 and ASP.NET Core 10 runtimes for your platform. Nuke's `PackAssetReviewTool` creates the package and `DeployAssetReviewTool` installs it for the current user.

### `--generate-manifest`

Scans the current directory (or `--asset-root`) for `.png` / `.svg` files and writes `_manifest.md` next to that root, then exits. Manifest entries use backtick-wrapped relative paths so the review app can load from the manifest instead of a full folder scan.

The generator does not invent storyboard sequences. If `_manifest.md` already has `Sequence:` / `Storyboard:` sections, those sections are kept and the flat asset list is replaced with a fresh scan.

### Storyboard sequences

Group ordered frames with an ATX heading and backtick paths relative to the manifest file. The sequence continues until the next heading. `Sequence:` and `Storyboard:` are equivalent. `{#id}` optionally pins a stable id.

```markdown
## Storyboards

### Sequence: Opening cinematic

- `boards/opening/01.png`
- `boards/opening/02.png`

### Storyboard: Boss intro {#boss-intro}

- `boards/boss/01.png`
- `boards/boss/02.svg`
```

HTML comments and fenced code blocks are ignored when reading sequences. Ordered sequences skip missing files and keep repeated paths so a hold frame can appear twice. Assets remain individually reviewable.

### Branches and loops in a manifest

Use `{#frame-id}` on each frame and indent `Branch: Label -> frame-id`, `Loop: Label -> frame-id`, or `Next: frame-id` beneath it.

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

The first frame is the entry point. Reach each frame through `Next` or `Branch`. A `Loop` returns to the same frame or to a frame with a forward path back to it. Frame IDs are case-insensitive and scoped to the storyboard. Invalid graphs report the manifest path and source line. `--generate-manifest` preserves the authored graph.

### Review mode

The toolbar **Review** control chooses **Assets** or **Storyboards**.

**Assets** reviews one file at a time. Previous/next follows the filtered asset list, and combat animation playback still runs for a combat asset opened from that grid.

**Storyboards** shows sequences with their review status. A branched storyboard opens in **Activity diagram**. Select **Simulate** to see each step's wireframe in the previewer. Use the **Previous**, **Next**, and **Restart storyboard** icons to retrace your path, advance, or start over. Branch and repeat buttons use the labels from the manifest. **Break** takes the loop's `Next` exit, presents its labeled exit choices, or ends on the final wireframe if no exit is defined. The filmstrip jumps to a frame. A linear storyboard opens in the previewer and can switch to the activity diagram. Approve and Request refinement apply to the current frame. Esc or Back returns to the storyboard list.

Activity diagrams require a local [PlantUML JAR](https://plantuml.com/download), Java, and [Graphviz](https://plantuml.com/graphviz-dot). Set `ASSET_REVIEW_PLANTUML_JAR` to the JAR path or place `plantuml.jar` in the `AssetReview` directory under your OS local application data directory. Java is resolved through `JAVA_HOME`, a `jre` subdirectory there, or `PATH`.

Feedback is written as JSON Lines to `.asset-review/feedback.jsonl` by default.
Each line records the asset path, decision, comment, preview context, and UTC timestamp.

The detail view previews assets over a selectable C64 palette background and supports
zoom through the slider or Ctrl+mouse wheel over the preview (plain wheel scrolls when the image overflows).

Use **Reload All** to rescan the asset root and refresh image URLs after regenerating PNG/SVG files.
Use **Clear Reviews** to remove non-approved review contents from the feedback file while keeping
the current approved decisions.
