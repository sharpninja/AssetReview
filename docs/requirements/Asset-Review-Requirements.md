# Asset Review requirements

Captured requirements for the `asset-review` tool. This repository did not include a requirements document or MCP workspace docs, so each item below is derived from `README.md`, `src/AssetReview.Tool/README.md`, and the review host CLI and page (manifest review, `--generate-manifest`, zoom and scroll, and in-flight combat storyboard sequences).

SharpNinja.aiUnit tests in `tests/AssetReview.AiUnit.Tests` load these rows through `AiUnitScenarioCatalog` and drive `asset-review` for each id. Screenshots land in `artifacts/aiunit/asset-review/`.

| Id | Title | Requirement | Source |
| --- | --- | --- | --- |
| FR-ASSETREVIEW-001 | PNG and SVG review grid | The review app lists PNG and SVG files from the asset root under the configured title. | README.md (Tool) |
| FR-ASSETREVIEW-002 | JSONL feedback record | Feedback is appended as JSON Lines recording the asset path, decision, comment, preview context, and a UTC timestamp. | README.md (Feedback) |
| FR-ASSETREVIEW-003 | C64 palette background | The detail view previews an asset on a selectable C64 palette background. | README.md (detail view) |
| FR-ASSETREVIEW-004 | Zoom and scroll preview | Zoom changes through the slider or Ctrl/Meta+mouse wheel, and a plain wheel scrolls the preview when the image overflows. | src/AssetReview.Tool/README.md |
| FR-ASSETREVIEW-005 | Reload All | Reload All rescans the asset root and shows files added after launch. | README.md (Reload All) |
| FR-ASSETREVIEW-006 | Clear non-approved reviews | Clear Reviews removes non-approved feedback and keeps the current approved decisions. | README.md (Clear Reviews) |
| FR-ASSETREVIEW-007 | Generate review manifest | --generate-manifest scans the asset root for PNG and SVG files, writes backtick-wrapped relative paths to _manifest.md, and exits. | README.md (--generate-manifest) |
| FR-ASSETREVIEW-008 | Prefer manifest over folder scan | When _manifest.md lists assets, the review index uses that list instead of every image in the folder. | README.md (manifest preference) |
| FR-ASSETREVIEW-009 | Search and status filters | Search text and the All, Open, Approved, and Refine filters limit which assets are shown. | Review page toolbar |
| FR-ASSETREVIEW-010 | Approve and request refinement | Approve and Request refinement save a decision and show it on the open asset. | Review panel actions |
| FR-ASSETREVIEW-011 | In-flight storyboard sequences | Hero and monster combat folders expose storyboard sequences that can play in flight, step a frame, and switch sequence. | CombatAnimationSets and the detail animation player |
| FR-ASSETREVIEW-012 | Version command | --version prints the asset-review version and exits successfully. | CLI --version |
| FR-ASSETREVIEW-013 | Server-only launch | --server-only serves the review UI without opening a desktop window and reports the URL and launch mode. | CLI launch modes |
| FR-ASSETREVIEW-014 | Reject path escape | Asset and feedback routes reject paths that escape the asset root. | Program.IsInside |
| FR-ASSETREVIEW-015 | Light and dark theme | The page applies light and dark themes from the theme query and from assetReviewSetTheme, which the embedded host calls. | AssetReviewWindow |
| FR-ASSETREVIEW-016 | Folder rail | The folder rail filters the grid and shows approval, refinement, and total counts. | Review page folder rail |
| FR-ASSETREVIEW-017 | Detail navigation | Next, previous, and back navigation move through the current asset list and return to the grid. | Detail navigation buttons |
| TR-ASSETREVIEW-001 | Reject unknown decisions | Feedback decisions other than approved or refinement are rejected. | POST /api/feedback |
| TR-ASSETREVIEW-002 | Validate port | A non-integer or out-of-range --port value exits with code 2 and an error on stderr. | TryParsePort |
| TR-ASSETREVIEW-003 | Skip non-review paths | Manifest generation and the review index skip non-image files and debug, screenshot, crawl-shot, and test-output paths, while still listing artifacts/art-approval. | EnumerateAssets and IsExcludedDebugAssetPath |

The embedded Avalonia window is the default `--embedded` host. The headless suite does not open that window; it serves the same page with `--server-only` and exercises `assetReviewSetTheme`, which the window calls after navigation.
