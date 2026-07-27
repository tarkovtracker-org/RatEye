<img src="media/RatLogo.png" height="100" align="right" alt="RatEye logo">

# RatEye

RatEye is the standalone image-processing library used by
[RatScanner](https://github.com/RatScanner/RatScanner) to recognize Escape from
Tarkov inspection titles and inventory icons.

The library deliberately does not know about RatScanner. Host applications own
screen capture, crop geometry, UI, application settings, and item-data
retrieval. RatEye owns OCR, marker detection, inventory-grid processing,
template matching, replay contracts, and stage timing.

Inventory icon scans use template matching first. When a template result is
low-confidence and traineddata is available, RatEye verifies the visible
top-right short name with OCR and only replaces the result when it maps to one
unique exact catalog short name. This keeps UI state decorations such as
wishlist and found-in-raid badges from deciding item identity.

## Supported environment

RatEye targets `netstandard2.0`, but its current OpenCvSharp native runtime is
Windows x64-only. The test and benchmark projects therefore run on Windows x64.

## Build and test

```bat
dotnet restore RatEye.sln
dotnet build RatEye.sln
dotnet test RatEye.sln
dotnet build -c Release RatEye.sln
```

The default test run contains deterministic unit and synthetic-image coverage.
Historical full game screenshots remain available as optional benchmark
fixtures and are not treated as ordinary unit tests.

## Use from source

Consumers may reference `RatEye/RatEye.csproj` directly. RatScanner uses that
source reference through a Git submodule, which allows engine and application
changes to be developed together without coupling their repositories or
requiring a NuGet publication for every development commit.

RatEye remains independently packageable. Its package version is owned by this
repository and is not tied to RatScanner's application version.

## Reproducible scan benchmark

`RatEye.Benchmarks` replays versioned `*.ratdiag.json` manifests from a
configurable fixture directory and writes a JSON report containing detected
items, confidence, expected-result status, total elapsed time, and engine stage
timings.

```bat
dotnet run --project RatEye.Benchmarks -- ^
  --fixtures C:\RatEyeFixtures ^
  --items C:\RatEyeData\items.json ^
  --locale C:\RatEyeData\locales\en.json ^
  --icons C:\RatEyeData\icons ^
  --traineddata C:\RatEyeData\traineddata ^
  --output C:\RatEyeFixtures\report.json
```

The equivalent environment variables are `RATEYE_FIXTURES`, `RATEYE_ITEMS`,
`RATEYE_LOCALE`, `RATEYE_ICONS`, and `RATEYE_TRAINEDDATA`. See
[`docs/benchmark-and-diagnostics.md`](docs/benchmark-and-diagnostics.md) for the
manifest contract and fixture guidance.

## License

See [LICENSE](LICENSE).
