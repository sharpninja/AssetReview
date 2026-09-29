# AssetReview.AiUnit.Tests

SharpNinja.aiUnit tests for the captured requirements in [`docs/requirements/Asset-Review-Requirements.md`](../../docs/requirements/Asset-Review-Requirements.md).

Each requirement id is a JSON scenario under `scenarios/`. `AiUnitScenarioCatalog` loads those files. `RequirementDriveTests` launches the `asset-review` tool and asserts the behavior. UI requirements are driven in headless Chromium against `asset-review --server-only`, which serves the same page the embedded Avalonia host loads.

## Run

From the repository root:

```powershell
dotnet test AssetReview.sln
dotnet test tests/AssetReview.AiUnit.Tests/AssetReview.AiUnit.Tests.csproj
./build.sh Test
```

Screenshots and a JSON sidecar per requirement are written to:

```text
artifacts/aiunit/asset-review/<REQUIREMENT-ID>.png
artifacts/aiunit/asset-review/<REQUIREMENT-ID>.json
```

Set `ASSET_REVIEW_SCREENSHOT_DIR` to write them somewhere else. Set `ASSET_REVIEW_BROWSER` or `CHROME_PATH` when Chromium is not `google-chrome` on `PATH`.

No display is required. The default `--embedded` Avalonia window is not started; on a machine without a display that window cannot open a WebView. `FR-ASSETREVIEW-013` covers `--server-only`, and `FR-ASSETREVIEW-015` covers `assetReviewSetTheme`, which the window calls.

## Optional frontier-model audit

`[AiFact]` / `[AiTheory]` skip the test body when no strategy resolves, so the driving tests are plain xUnit `[Theory]` rows (one per requirement) that always run. A matching `[AiTheory]` sends each screenshot to the active strategy.

`appsettings.aiunit.json` selects the `grok` HTTP strategy. With no `XAI_API_KEY` or `AIUNIT_API_KEY`, those audits skip. To run them:

```powershell
$env:XAI_API_KEY = "<key>"
$env:ASSET_REVIEW_VISUAL_AUDIT = "true"
dotnet test tests/AssetReview.AiUnit.Tests/AssetReview.AiUnit.Tests.csproj --filter "FullyQualifiedName~VisualAudit"
```

`AIUNIT_STRATEGY` can select another strategy from the file shape documented by SharpNinja.aiUnit. Do not point `ActiveStrategy` at a `cli` strategy unless that CLI is installed; a CLI strategy resolves without an API key and the audit would then try to spawn it.
