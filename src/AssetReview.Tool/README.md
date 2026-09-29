# Asset Review Tool

> Extracted from BBCrawler into the standalone [AssetReview](../../) repository. Package id remains `SharpNinja.AssetReview.Tool`; command remains `asset-review`.

Launches a local review app for PNG and SVG assets in the current workspace.

```powershell
asset-review
asset-review --asset-root artifacts/art-approval/full-refresh --title "BBCrawler Asset Review"
```

Feedback is written as JSON Lines to `.asset-review/feedback.jsonl` by default.
Each line records the asset path, decision, comment, preview context, and UTC timestamp.

The detail view previews assets over a selectable C64 palette background and supports
zoom through the slider or mouse wheel over the preview.

Use **Reload All** to rescan the asset root and refresh image URLs after regenerating PNG/SVG files.
Use **Clear Reviews** to remove non-approved review contents from the feedback file while keeping
the current approved decisions.
