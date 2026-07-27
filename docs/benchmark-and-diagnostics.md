# Benchmark and scan diagnostics

## Purpose

RatEye stage timings and replay manifests make scan failures measurable without
adding telemetry. A host application exports a captured bitmap and a neutral
sidecar manifest only when the user explicitly requests it. The benchmark
replays those bundles locally.

## Bundle layout

```text
fixture-name/
  capture.png
  fixture-name.ratdiag.json
```

The manifest uses schema version 1:

```json
{
  "schemaVersion": 1,
  "id": "f1-inspection-1080p",
  "scanType": "inspection",
  "imageFile": "capture.png",
  "expectedItemIds": ["5710c24ad2720bc3458b45a3"],
  "configuration": {
    "scale": 1.0,
    "language": "English",
    "optimizeHighlighted": false,
    "useStaticIcons": true,
    "scanRotatedIcons": true
  },
  "context": {
    "capturedAtUtc": "2026-07-27T12:00:00Z",
    "applicationVersion": "4.x",
    "captureX": 0,
    "captureY": 0,
    "captureWidth": 1920,
    "captureHeight": 1080,
    "displayX": 0,
    "displayY": 0,
    "displayWidth": 1920,
    "displayHeight": 1080,
    "dpiScale": 1.0,
    "cursorX": null,
    "cursorY": null
  }
}
```

Supported `scanType` values are:

- `inspection`: one already-cropped inspection capture
- `multi-inspection`: a full-screen capture containing inspection windows
- `inventory`: an inventory capture; optional cursor coordinates are relative
  to the captured bitmap
- `icon`: one already-cropped item icon

`expectedItemIds` is optional for user diagnostic bundles. When present, the
runner compares it with detected IDs and exits with code 2 if any fixture
mismatches. Exit code 1 indicates invalid input or runner failure.

## Running

```bat
dotnet run --project RatEye.Benchmarks -- ^
  --fixtures C:\RatEyeFixtures ^
  --items C:\RatEyeData\items.json ^
  --locale C:\RatEyeData\locales\en.json ^
  --icons C:\RatEyeData\icons ^
  --traineddata C:\RatEyeData\traineddata ^
  --output C:\RatEyeFixtures\report.json
```

The runner searches recursively for `*.ratdiag.json`. `--icons` is needed for
template matching; `--traineddata` is needed for OCR. The report records
detections, confidences, expected-result status, and timings such as marker
search, OCR preprocessing/recognition, item matching, inventory grid
detection/parsing, and icon template matching.

## Fixture contribution guidance

- Prefer Escape from Tarkov's screenshot key so overlays are excluded and
  native resolution is preserved.
- For name scans, include the full inspect window or the exact crop exported by
  the host.
- For icon scans, preserve the inventory/stash capture and cursor-relative
  position.
- Record expected item IDs, language, display size, DPI scale, and RatEye scale.
- Remove unrelated personal information from screenshots before sharing.
- Do not add large game-screenshot sets to consumer repositories. Keep them in
  a separately reviewed fixture source or use a local directory.
- Preserve known failures in reports; do not change expected IDs merely to make
  a benchmark pass.
