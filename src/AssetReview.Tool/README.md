# Asset Review Tool

> Extracted from BBCrawler into the standalone [AssetReview](../../) repository. Package id remains `SharpNinja.AssetReview.Tool`; command remains `asset-review`.

Launches a local review app for PNG and SVG assets in the current workspace.

```powershell
asset-review
asset-review --asset-root artifacts/art-approval/full-refresh --title "BBCrawler Asset Review"
asset-review --generate-manifest
```

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

HTML comments and fenced code blocks are ignored when reading sequences. Missing files are skipped. Repeated paths are kept so a hold frame can appear twice. Assets remain individually reviewable.

### Review mode

The toolbar **Review** control chooses **Assets** or **Storyboards**.

**Assets** reviews one file at a time. Previous/next follows the filtered asset list, and combat animation playback still runs for a combat asset opened from that grid.

**Storyboards** shows sequences instead of individual files. The status filter keeps sequences that are open, fully approved, or marked for refinement. Opening one reviews the whole sequence: previous/next and the arrow keys stay on its frames. Approve and Request refinement still record a decision for the current frame. Esc or Back returns to the storyboard list. Switching **Review** leaves the current detail view and shows that mode's list.

Feedback is written as JSON Lines to `.asset-review/feedback.jsonl` by default.
Each line records the asset path, decision, comment, preview context, and UTC timestamp.

The detail view previews assets over a selectable C64 palette background and supports
zoom through the slider or Ctrl+mouse wheel over the preview (plain wheel scrolls when the image overflows).

Use **Reload All** to rescan the asset root and refresh image URLs after regenerating PNG/SVG files.
Use **Clear Reviews** to remove non-approved review contents from the feedback file while keeping
the current approved decisions.