# RatEye architecture notes (engine-owned view)

## Header

- **Analyzed:** 2026-08-07
- **Commit:** `24f8806` (v4.0.0-27)
- **Scope:** engine-owned concerns only — public contracts, configuration API, lifecycle, threading/locking, nullability observations, and boundaries expected from consuming applications. Parent-app findings (WPF host, MudBlazor UI, app DI, `RatScannerMain`) deliberately do not appear here.
- **Method:** static evidence pass; no source changes. Findings carry `Status` and `Last verified` so this file can be updated rather than rediscovered.

## Boundaries (from `AGENTS.md`, restated as contracts)

- RatEye never references RatScanner (or any host application).
- Host applications own capture, crop geometry, and screen state; RatEye processes bitmaps and neutral replay manifests.
- Engine-internal regression tests live in `RatEyeTest`; consumers get no `InternalsVisibleTo`.
- Deterministic disposal of Tesseract, OpenCV, bitmap, marker, and icon-manager resources is a hard requirement.

## Findings

### 1. Public configuration surface mixes instance state and process-wide statics

- **Severity:** Medium | **Confidence:** High | **Status:** Open | **Last verified:** `24f8806`
- **Evidence:** `Config/Config.cs:13` `public static bool LogDebug { get; set; }`; `Config/Path.cs:83,88` `public static string Debug { get; set; }`, `public static string LogFile { get; set; }`; consumed applications write these directly (RatScanner sets `Config.Path.LogFile = "RatEyeLog.txt"`, `Config.Path.TesseractLibSearchPath`, `Config.LogDebug`). The rest of the configuration tree is per-instance (`Config.PathConfig`, `Config.ProcessingConfig`).
- **Why it matters:** static configuration is process-global even though the engine supports multiple instances; a host rebuilding an engine carries stale static path state across instances; the public API does not make the static/instance split visible.
- **Target:** move `Path.Debug`/`LogFile`/`LogDebug` onto the instance `Config`/`Path` objects with defaults, keeping the static members as deprecated forwarding accessors (or removing them in a major version).
- **Scope:** Medium | **Sequencing:** before any nullability migration that touches `Config` (see #5).

### 2. Engine lifecycle: disposal mutates caller-supplied configuration

- **Severity:** Medium | **Confidence:** High | **Status:** Open | **Last verified:** `24f8806`
- **Evidence:** `RatEyeEngine.Dispose()` → `DisposeCore` releases the icon manager, Tesseract engines, and inspection marker **and mutates the caller-supplied `Config`** (documented in `RatEyeEngine.cs` remarks). `DisposeStrict` throws on cleanup failure; `Dispose` swallows cleanup errors into `CleanupFailure`.
- **Why it matters:** hosts rebuilding an engine (e.g. after a catalog refresh) must know that disposing the old engine invalidates state on a `Config` they may still hold; accidental reuse of a disposed `Config`'s marker/icon-manager references fails in non-obvious ways.
- **Target:** document the ownership rule in the public API docs (whoever owns the `Config` owns its engine), or detach engine-owned resources from the caller's `Config` into engine-private state.
- **Scope:** Medium | **Sequencing:** with #1 (both are API-surface contracts).

### 3. Rebuild expectations are implicit

- **Severity:** Medium | **Confidence:** Medium | **Status:** Open | **Last verified:** `24f8806`
- **Evidence:** RatScanner rebuilds by constructing a new `RatEyeEngine(config, database)` and disposing the previous one (`RatScannerMain.SetupRatEye`); the engine itself offers no rebuild/swap API. `NewInspection`/`NewMultiInspection` return objects that hold engine-owned resources (`Icon.cs`, `Inspection.cs`, `MultiInspection.cs` state machines with `Dispose`).
- **Why it matters:** a host that needs to swap databases (catalog refresh) or rescale (monitor change) must hand-roll teardown/creation ordering and serialize it against in-flight processing; mistakes leak native resources.
- **Target:** an explicit `EngineRebuildContext` or documented swap pattern (create-new-then-dispose-old, quiesce before dispose) in the public docs.
- **Scope:** Small (documentation) to Medium (API) | **Sequencing:** after #1–2.

### 4. Thread-safety contract is undocumented

- **Severity:** Low–Medium | **Confidence:** Medium | **Status:** Open | **Last verified:** `24f8806`
- **Evidence:** per-instance processing is not internally synchronized (hosts serialize with external locks — RatScanner uses `NameScanLock`/`IconScanLock`); `IconManager` uses `ReaderWriterLockSlim` for static-icon correlation data (`IconManager.cs`); `ProcessingTimings` is mutable during processing.
- **Why it matters:** hosts guessing the contract either over-serialize (scan latency) or race (native resource corruption). The one documented guarantee today is lock ordering *inside a host*, which the engine cannot enforce.
- **Target:** state the contract in the public docs: one engine instance = one processing flow at a time; `IconManager` lookups are internally safe; timings are valid after the processing call returns.
- **Scope:** Small | **Sequencing:** independent.

### 5. Nullability/API-contract observations (pre-migration baseline)

- **Severity:** Medium | **Confidence:** High | **Status:** Open | **Last verified:** `24f8806`
- **Evidence:** nullable reference types are not enabled (netstandard2.0, LangVersion 9). A temporary enablement pass measured ~78 unique CS86xx warnings: CS8618 ×18 (engine-wired fields such as `IconManager._staticIconSourceWatcher`, `Config.IconManager`, `TesseractEngine`), CS8603 ×13 (possible-null returns concentrated in `IconManager` — `GetItem(string)` returns `Item` but can return null), CS8625 ×23 (null literals into non-nullable parameters), plus CS8600/8604/8602/8601. Public field-heavy model: `Vector2`, `Config.Processing.Icon`, `Inspection`, `Icon` expose public fields.
- **Why it matters:** enabling nullability is an API-contract change — `Item?`, `Mat?`, `string?` signatures will ripple into every consumer (RatScanner's `ItemScan` mapping already handles nulls defensively). The CS8618 set are initialization-invariant claims (`null!` is only correct where the architecture guarantees it — e.g. engine-wired fields), not a mechanical fix.
- **Target:** dedicated migration: (a) annotate public returns honestly (`Item?`), (b) convert invariant fields to `required`/ctor-initialized where possible, (c) enable `Nullable` in `RatEyeTest` first, (d) consider `LangVersion` bump in a major version.
- **Scope:** Large | **Sequencing:** last — after the API-surface work in #1–3 so the annotations land once.

### 6. Results expose implementation types

- **Severity:** Low | **Confidence:** Medium | **Status:** Open | **Last verified:** `24f8806`
- **Evidence:** `Inspection`/`Icon` expose `RatStash.Item`, `ProcessingTimings`, marker bitmaps, and scan-mode internals; hosts reach into `inspection.Timings`, `Config.ProcessingConfig.InspectionConfig.Marker` for tooltip geometry.
- **Why it matters:** every internal rename ripples into hosts; results are not stable as a serialization contract (relevant for the replay-manifest diagnostics story).
- **Target:** keep the internal types, but treat the read paths hosts actually use (item, confidence, position, timings snapshot) as the documented result contract.
- **Scope:** Small | **Sequencing:** with #4 (documentation).

## Not a problem (engine-side leads cleared)

- **Disposal idempotency:** `RatEyeEngine.DisposeCore` guards on `_disposed`; double-dispose is safe; `DisposeStrict`/`Dispose` semantics are explicit.
- **Lock ordering:** `IconManager`'s reader/writer lock usage is confined and does not nest with host locks.
- **No `async void` / fire-and-forget inside the engine**; processing is synchronous and host-driven.
- **Tesseract/OpenCV lifecycle:** released deterministically in `DisposeCore` (`TryCleanup` aggregation with `CleanupFailure`); the `TesseractEngine` null-out on cleanup is deliberate.
- **`Path.Debug`/`LogFile` property conversion** (CA2211 fix) preserved settable behavior — verified no in-repo assignment broke; `RatScannerMain` assigns `LogFile` intentionally.
- **Hash formatting (CA1305 fix):** `GetHash`/`SHA256Hash` are now culture-invariant; `RatEyeTest` cache tests (69) pass — cache-key stability confirmed.

## Boundary expectations for consuming applications

1. Own capture, crop geometry, and screen state; pass bitmaps in, get results out.
2. Treat one engine instance as single-flow: serialize processing, rebuild by create-new-then-dispose-old.
3. Do not reach into `Processing` internals for results; consume `Inspection`/`Icon` public reads.
4. Expect `Config` mutation on engine disposal until #2 lands.
5. Prepare for `?` annotations on lookup results (item/mat lookups can legitimately miss) when nullability lands.
