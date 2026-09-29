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

Feedback is written as JSON Lines to `.asset-review/feedback.jsonl` by default.
Each line records the asset path, decision, comment, preview context, and UTC timestamp.

The detail view previews assets over a selectable C64 palette background and supports
zoom through the slider or Ctrl+mouse wheel over the preview (plain wheel scrolls when the image overflows).

Use **Reload All** to rescan the asset root and refresh image URLs after regenerating PNG/SVG files.
Use **Clear Reviews** to remove non-approved review contents from the feedback file while keeping
the current approved decisions.