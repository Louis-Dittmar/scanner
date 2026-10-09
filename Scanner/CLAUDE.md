# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Scanner ("Scanner for Windows") is an MSIX-packaged scanner app for Windows 11, published in the Microsoft Store. It is a **WinUI 3 / Windows App SDK desktop app on .NET 10**. It drives WIA-compatible scanners, edits the results (crop, rotate, ink, filters), and saves to PDF or image files.

## Keeping this file up-to-date

Treat this document as part of the codebase: when a change you make invalidates something here, update it in the **same change**. In particular, revise the relevant section when you:

- Add, remove, or rename a service — keep the DI registration notes (`App.xaml.cs`) and `Services/Interfaces/` guidance accurate.
- Introduce a new architectural pattern, message type convention, or `IProjectAction`, or change how the project/undo-redo/persistence layers fit together.
- Change the build, restore, deploy, or test workflow (commands, platforms, target framework, test runner).
- Add or swap a major dependency or integration (scanning, OCR, AI, PDF, telemetry, native interop) — update **Key integrations**.
- Change localization tooling, secrets handling, or the CI pipeline (`.github/workflows/build.yml`, `nuget.config`).
- Add or move a top-level project, window, or directory described under **Repository layout**.

Keep edits surgical and high-level: describe the "big picture" that requires reading several files to grasp. Do not turn this into an exhaustive file listing, and remove guidance that has become stale rather than letting it drift.

## Repository layout

The git root is one level above this file (`..`, contains `.git`, `README.md`, `.github/`). This directory (`Scanner/`) holds the solution; build/test commands run from here.

- `Scanner.slnx` — the solution (XML SLNX format, not `.sln`). References the main app, the test projects, `Scanner.Core` and the Tesseract submodule.
- `Scanner/` — the main app project (`Scanner.csproj`, root namespace `Scanner`).
- `Scanner.Core/` — fork addition: platform-independent document processing (`net10.0`, AnyCPU, no WinUI/WinRT): document detection, white correction, Unlimited-OCR output parsing, PDF creation (PDFsharp with embedded Liberation Sans) and the `DocumentProcessor` pipeline. Referenced by the app.
- `ScannerCoreTests/` — plain MSTest/VSTest unit tests for `Scanner.Core`; run anywhere with `dotnet test ScannerCoreTests` (PDF text is verified with PdfPig).
- `ScannerTests/` — MSTest + FlaUI end-to-end UI automation project.
- `Scanner/OcrService/tests/` — pytest tests for the Python AI service (`python -m pytest Scanner/OcrService/tests`, run with `--fake-model`, no GPU needed).
- `../tools/install/` — `Installieren.cmd`/`Deinstallieren.cmd`/`LIESMICH.txt`, copied into the CI package artifact.
- `../Tesseract/` — git **submodule** (fork `simon-knuth/tesseract-arm64`) referenced directly as a project. Run `git submodule update --init --recursive` before first build.

## Build, run, and test

The app is platform-specific — there is **no AnyCPU**. You must pick a platform (`x86`, `x64`, or `ARM64`).

```powershell
# Restore (populates obj/ with RuntimeIdentifiers — required before first build)
msbuild Scanner.slnx /t:Restore /p:Configuration=Debug

# Build a specific platform
msbuild Scanner.slnx /p:Configuration=Debug /p:Platform=x64
```

Day-to-day, build/run/deploy from **Visual Studio** with the `Scanner` project set as startup and a platform selected — MSIX deployment registers the package locally, which the UI tests depend on.

### Tests

`ScannerTests` are **FlaUI UI-automation tests that launch the installed Store app** (`Application.LaunchStoreApp` with the AUMID in `ScannerTests/Constants.cs`). They are not in-process unit tests:

- The app MSIX **must be deployed locally first** (build/deploy `Scanner` from VS), or the launch fails. CI installs the signed package it built.
- Tests launch the app with `--ui-test` (`App.IsUiTestMode`, `App.Fork.cs`): first-run dialogs are skipped and the AI model is replaced by `UiTestOcrEngine`, so the end-to-end test (debug scanner → document window → PDF in the output folder, here `TempState\UiTestOutput`) runs without a GPU. Tests must not run in parallel (single-instance app, `[assembly: DoNotParallelize]`).
- Tests drive the real UI by AutomationId. Element IDs are defined once in `Scanner/Tests/AutomationIds.cs` (namespace `Scanner.Tests`) and referenced from both the app XAML and the tests — add an ID there when you need to target a new control. `ScannerTests` has **no `ProjectReference` to the app**: it links that one file in as `<Compile>`, so it stays AnyCPU and platform-agnostic (a reference to the platform-specific, self-contained app project causes `MSB3270`/`NETSDK1151`). Keep `AutomationIds.cs` free of dependencies — plain constants only.
- Uses `EnableMSTestRunner` (Microsoft.Testing.Platform). Run via `dotnet test ScannerTests/ScannerTests.csproj` or the VS Test Explorer.
- Test images are found relative to the test binaries (`Scanner/Resources/Test Images`) or via `SCANNER_TEST_IMAGES`. On failure a screenshot is attached to the test results.

To exercise scanning **without physical hardware**, the UI exposes an "Add debug scanner" action (`Models/ScanningDevices/DebugScanner.cs`) that lets you scan from local image files.

## Architecture

Strict **MVVM** with `CommunityToolkit.Mvvm`. Three layers under `Scanner/`: `Views/` (XAML + code-behind), `ViewModels/`, `Models/`. ViewModels and models derive from `ObservableObject`/`ObservableRecipient`; commands are `[RelayCommand]`.

**Dependency injection** — all services are registered as singletons in `App.xaml.cs` via `Ioc.Default.ConfigureServices(...)`. Resolve with `Ioc.Default.GetRequiredService<IFoo>()`. Each service in `Services/` has an interface in `Services/Interfaces/` and is referenced through that interface. When adding a service, register it in `App.xaml.cs` and add the interface.

**Messaging** — components communicate through `WeakReferenceMessenger.Default` (CommunityToolkit) rather than direct references. Message types live in `Messages/` (e.g. `ShowSaveOptionsDialogMessage`, `ApplyTemplateMessage`). Views/dialogs typically subscribe to a `Show…Message` to present themselves. Prefer a message over coupling a ViewModel to a View.

**Project model** (`Models/Project/`) — the central editing concept. `ProjectBase` (abstract `ObservableRecipient`) is specialized by `PdfProject` and `MultiFileProject`. `IProjectService` owns the single `CurrentProject`, page selection, scan/edit state machine (`ScanState`), and persistence. Note: `ProjectBase` pulls its dependencies via `Ioc.Default` static fields rather than constructor injection.

**Undo/redo** — a command pattern. Every editing operation is an `IProjectAction` in `Models/ProjectActions/` (e.g. `CropPagesAction`, `RotatePagesAction`, `RenameAction`). Apply through `IProjectService.ApplyActionAsync`; the service maintains `UndoStack`/`RedoStack`. Add a new `IProjectAction` to support a new editing operation rather than mutating pages directly.

**Save safety (change tracking & concurrency)** — dirty state is two monotonic revision numbers on `ProjectBase`, not a mutable flag: every content mutation calls `BumpRevision()` (bumping `contentRevision`, raising `ContentChanged`); a save captures the revision at snapshot time and, on success, advances `savedRevision` to it, so `IsSaved` is `savedRevision >= contentRevision`. This makes "mark clean" correct by construction — an edit landing during a save keeps the project dirty. Effect values (`SetBrightness`/`SetContrast`) are committed synchronously so a save never serializes a stale value. A save holds `saveSemaphore` for its whole duration (including the unlocked encode that reads live source files), so any edit that deletes/moves a page's former source file must do so via `RetireSourceFileAsync` (which takes `saveSemaphore`) **after** releasing the edit locks, never during editing. Auto-save (`ProjectService`) is debounced on `ContentChanged` plus a slow self-healing safety-net timer.

**Persistence** — EF Core with SQLite, split into three `DbContext`s in `Data/`: `KnownScannersDbContext`, `ProjectHistoryDbContext`, `TemplatesDbContext`, each fronted by a service (`KnownScannersService`, `ProjectHistoryService`, `TemplatesService`).

**Windows & app lifecycle** — top-level windows in `AppWindows/`: `MainWindow`, `SettingsWindow`, `FeedbackWindow` (latter two created on demand via `App.ShowSettings`/`ShowFeedback`) and the fork's `DocumentWindow` (via `ShowDocumentWindowMessage`, `App.Fork.cs`). All derive from `WindowBase` (itself a WinUIEx `WindowEx`), which sets up the backdrop, icon and extended title bar, and keeps the system-drawn caption buttons in sync with the content's theme — derive new windows from it (XAML root `appwindows:WindowBase`). The app theme setting is applied once via `Application.RequestedTheme` in the `App` constructor (only possible before any UI exists, hence a restart is required). The app is **single-instance**: `Program.cs` defines a custom `Main` (`DISABLE_XAML_GENERATED_MAIN` is set in the csproj) that uses `AppInstance` key registration and redirects activation to the existing instance.

### Key integrations

- **Scanning**: `Windows.Devices.Scanners` (WIA) via `ScannerDiscoveryService`; hardware in `Models/ScanningDevices/HardwareScanner.cs`.
- **OCR**: Tesseract (the submodule). `OcrService` + training data under `Resources/Tesseract Training Data/`. Its native DLLs need the VC++ runtime, provided via the `Microsoft.VCLibs.Desktop` `SDKReference` (a `Microsoft.VCLibs.140.00.UWPDesktop` package dependency). If creating the engine fails, OCR degrades to "unavailable" (`IOcrService.IsAvailable`). The `OcrService` constructor must never throw, since the service is resolved from `ProjectBase`'s static initializer.
- **AI features**: `CopilotRuntimeService` uses the Windows Copilot Runtime (on-device models, e.g. Phi Silica) — gated behind Copilot+ hardware availability.
- **PDF**: PDFsharp.
- **AI text recognition** (fork addition): `OcrService/server.py` is a headless Python HTTP service (127.0.0.1, token in `X-Token`) running the `baidu/Unlimited-OCR` model via Transformers on CUDA; `/ocr` returns the **raw** model output (`<|det|>label [x1, y1, x2, y2]<|/det|>content`, boxes 0..1000), which the app parses with `Scanner.Core`'s `UnlimitedOcrParser` (single source of truth, unit-tested). `AiOcrService` installs it on demand with `uv` (private Python 3.12, PyTorch cu128 from `requirements-torch.txt`, rest from `requirements.txt`; a hash of both files marks the installation as current) into `LocalCache\AiOcr` (removed with the app) or a user-chosen folder (always a `Scanner Paperless KI` subfolder; uninstall only deletes folders it created). The model is loaded on app launch (`App.Fork.cs`, if enabled) and unloaded on close (`ShutdownForAppExit` + the service watching the app's PID; `LingerMinutes`/`IdleUnloadMinutes` default to 0). The service must log to its own file (never a pipe). Launch prompt: `ShellView.AiSetup.cs` shows `AiSetupDialogView` once (location, free space, "don't ask again"); `AiSetup` runs the installation in the background.
- **Document pipeline** (fork addition): `DocumentPipelineService` runs automatically after a scan (`ScanCompletedSuccessfully`, then debounced until `IsProcessRunning` is false) or via the project menu. It reads the saved file(s) if the project is saved, otherwise the pages' current images (`ProjectBase.ReadPageImagesAsync`), decodes them with `WinRtImaging`, and runs `Scanner.Core`'s `DocumentProcessor`: `DocumentDetector` (crop to the sheet) → `WhiteCorrection` (only for detected documents; pictures only white-balanced) → recognition (`AiOcrEngine`, raw results cached by image hash in the install folder) → `DocumentPdfComposer` (`Reconstructed`: real text, tables, pictures cut from the scan; or `ScanWithTextLayer`). The PDF goes to the output folder (default `Documents\Scanner Paperless`). Progress is published as a `DocumentJob` (`ViewModels/DocumentJob.cs`, UI thread only) shown by `DocumentWindow`/`DocumentProcessingView` (steps, page with region overlay, text/areas/PDF preview via WebView2).
- **Paperless-ngx** (fork addition): `PaperlessService` talks to the Paperless-ngx REST API with `HttpClient` (token auth). The server address lives in its own `LocalSettings` container (not `SettingsService`, which logs values), the API token in the Windows `PasswordVault` — never log either. The project menu's "Send to Paperless" reads the saved target file(s) via `ProjectBase.TryReadSavedFilesForUploadAsync` (takes the save/project locks like copy/share) and sends a `ShowPaperlessUploadDialogMessage`; uploads are deliberately decoupled from saving because auto-save saves continuously. Fork-specific code is kept in separate files (`*.Paperless.cs` partials, `Paperless*` view models, `SettingsViewPaperless`) to keep merges from upstream simple.
- **Native interop**: `Microsoft.Windows.CsWin32` (source-generated P/Invoke; see `NativeMethods.txt` if present) and CsWinRT.
- **Telemetry / logging**: Sentry (`SentryService`) and Serilog (`LogService`, async file sink). Crashes/unobserved exceptions are funneled through `App.xaml.cs` handlers with a plain-text fallback log. Log through `ILogService.Log` (a `CallerLogger`) with Serilog message templates — it auto-enriches each entry with the calling member name, so **do not** prefix messages with the method/class name. Use `Information` for meaningful user actions (scan, save, edit-action applied/undone, dialog opened, scanner selected, …), keeping volume low (Sentry storage is limited) and **never logging PII** — no file names or folder/database paths. Classes serialized often (`ScanOptions`, `ScanResolution`, `ScanArea`, `ScanMergeConfig`, `IScanningDevice`) have PII-free `Destructure.ByTransforming` policies in `LogService.ConfigureCustomDestructuring`; log those with the `{@X}` operator and extend that method for new such types.

### Localization

UI strings are localized via **ReswPlus** (`Resources/Strings/`), with ~20 languages. ReswPlus generates strongly-typed accessors from the `.resw` files — edit the resource files, not generated code.

## Conventions

- `Nullable` is enabled across both projects; honor nullable annotations.
- Source files use distinctive banner comment blocks (`// DECLARATIONS //…`, `// METHODS //…`) to section classes — match this when editing existing files.
- WinUI XAML lives beside its code-behind (`*.xaml` + `*.xaml.cs`); `EnableXamlSourceGeneration` is on.

## Secrets & CI

- `Resources/Secrets.resx` ships with a literal placeholder `SENTRY_DSN_GOES_HERE`. The GitHub Actions release build (`.github/workflows/build.yml`) replaces it with the real DSN from secrets — **do not commit a real DSN** into this file.
- `build.yml` (upstream release build) builds the MSIX for all three platforms, signs it with a PFX from secrets, and creates a Sentry release. It is **manual-trigger only** (`workflow_dispatch`) and builds `Scanner/Scanner.csproj` directly rather than the solution, so `ScannerTests` is not built during packaging. Local builds do not need the certificate for `Debug`.
- `ci.yml` (fork) runs on every push/PR: `ScannerCoreTests` on Linux, the OCR service pytest on Linux and Windows, and on `windows-latest` a Release x64 MSIX build signed with a self-signed `CN=Louis Dittmar` certificate (or `SIGNING_PFX_BASE64`/`SIGNING_PFX_PASSWORD` secrets), installed on the runner for the FlaUI UI tests. Compiler errors are collected into one annotation. The package (plus `tools/install` scripts) is uploaded as artifact `Scanner-Paperless-x64`; test results, screenshots and app logs as `ui-test-results`.
