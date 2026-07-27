# RatEye agent instructions

## Scope

RatEye is an independently buildable and packageable image-processing library.
It may be consumed by RatScanner and other applications, but it must never
reference RatScanner.

## Mandatory boundaries

1. Keep WPF, Blazor, WebView, HTTP clients, app configuration persistence, and
   screen capture out of RatEye.
2. Host applications own capture and crop geometry. RatEye processes bitmaps
   and neutral replay manifests.
3. Keep the current Windows x64 native-runtime constraint explicit. Do not
   claim x86, Linux, or macOS support without compatible native dependencies
   and validation.
4. Keep OpenCvSharp managed/native package versions paired.
5. Preserve deterministic disposal of Tesseract, OpenCV, bitmap, marker, and
   icon-manager resources.
6. RatEye owns its package metadata and version independently of RatScanner.
7. Large or copyrighted game screenshots are optional local/legacy benchmark
   fixtures, not mandatory CI unit-test inputs.
8. Engine-internal regression tests belong to `RatEyeTest`; consumers must not
   require `InternalsVisibleTo` access.

## Validation

```bat
dotnet restore RatEye.sln
dotnet build RatEye.sln
dotnet test RatEye.sln
dotnet build -c Release RatEye.sln
```

For processing behavior, also replay the relevant fixture directory with
`RatEye.Benchmarks` and inspect its JSON report. A build alone does not prove
scan accuracy.
