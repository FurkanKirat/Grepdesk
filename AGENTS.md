# AGENTS.md

Instructions and guidelines for AI coding agents working on the **Grepdesk** repository.

---

## 1. Project Overview

**Grepdesk** is a high-performance desktop file manager, standalone CLI (`grepdesk`), and utility suite built with **.NET 10 (`net10.0`)** and **Avalonia UI 11.3**. It combines folder browsing, instant file name indexing, deep content search (`.txt`, code, `.pdf`, `.docx`, `.xlsx`, `.pptx`), disk usage analysis, space cleanup & duplicate detection, deterministic folder organization, a built-in document/media viewer & editor, and multi-core zip/copy/move engines integrated into the OS file manager.

---

## 2. Build, Run & Test Commands

Always verify changes by building and running the test suite before finishing a task. See [docs/BUILD.md](docs/BUILD.md) for full packaging and release details.

```powershell
# Build the entire solution
dotnet build

# Run the desktop application or standalone CLI
dotnet run --project Grepdesk.UI
dotnet run --project Grepdesk.Cli -- --help

# Run unit and integration tests (fast, runs by default)
dotnet test
dotnet test Grepdesk.Tests

# Run a single test class or method
dotnet test Grepdesk.Tests --filter "FullyQualifiedName~ZipCompressorTests"

# Run performance guard tests (skipped unless GREPDESK_PERF=1 is set)
$env:GREPDESK_PERF = "1"; dotnet test Grepdesk.Tests -c Release --filter Category=Performance

# Run benchmarks (smoke run or full suite; CLI benchmark logs to %LOCALAPPDATA%\Grepdesk\benchmark.log)
dotnet run -c Release --project Grepdesk.Benchmarks -- --scale 0.1 --rounds 1
dotnet run --project Grepdesk.Benchmarks -c Release
publish\win-x64\Grepdesk.UI.exe --extract-to --benchmark archive.zip

# Build CLI tool (actions: all, build, test, publish, installer, portable, clean)
python build.py
python build.py test
python build.py publish --os windows        # or linux / macos
python build.py --no-test --no-installer -c Debug
# Or on Windows PowerShell:
.\build.ps1

# Manual self-contained publish & Inno Setup installer (Windows)
dotnet publish Grepdesk.UI -p:PublishProfile=win-x64
dotnet publish Grepdesk.Cli -p:PublishProfile=win-x64
iscc setup.iss
```

### Linux Test Execution (via Docker on Windows)
```powershell
docker run --rm -v "${PWD}:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c `
  "cp -r /src /work && cd /work && find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + && dotnet test Grepdesk.Tests"
```

---

## 3. Architecture & Layering Rules

```text
Grepdesk.UI ──► Grepdesk.Core ◄── Grepdesk.Windows / Grepdesk.Linux / Grepdesk.MacOS
```

1. **Keep `Grepdesk.Core` Pure**:
   - `Grepdesk.Core` must **never** reference Avalonia, Win32 P/Invoke, COM, or `Microsoft.Win32.Registry`.
   - All core algorithms (indexing, archiving, transfer, disk analysis, cleanup, grouping, document models) belong in `Grepdesk.Core` so they remain cross-platform and directly unit-testable.
2. **Use Platform Interfaces**:
   - OS-specific operations must be defined as interfaces in `Grepdesk.Core` (`IPlatformShell`, `IShellIntegration`, `IDriveKindProvider`, `IFileCopier`, `IFileClipboard`, `IShellPreview`, `IShellIcons`, `IMediaPlayback`).
   - Implement them per OS in `Grepdesk.Windows`, `Grepdesk.Linux`, and `Grepdesk.MacOS`, and wire them in `Grepdesk.UI/Helpers/PlatformShellFactory.cs` behind `#if PLATFORM_WINDOWS` / `PLATFORM_LINUX` / `PLATFORM_MACOS`.
3. **Never Call `Process.Start` Directly from `Grepdesk.UI`**:
   - Route all external process launches (opening files, revealing in file manager, opening terminals, launching editors) through `IPlatformShell` and inspect the returned `ShellActionResult`.

---

## 4. Mandatory Codebase Invariants

### 4.1 Bilingual Localization (`en.json` & `tr.json`)
- Every user-facing string in `Grepdesk.UI` **must** be added to both:
  - `Grepdesk.UI/Assets/Lang/en.json` (English)
  - `Grepdesk.UI/Assets/Lang/tr.json` (Turkish)
- Retrieve strings via `LocalizationService.Instance["Key"]` or `Loc.Get("Key", args)`. Do not hardcode English or Turkish UI strings in C# or AXAML.

### 4.2 Registry & Installer Synchronization (`InstallerTests`)
- `Grepdesk.Windows/WindowsShellIntegration.cs` exposes `WindowsShellIntegration.AllKeyPaths` listing every `HKCU` registry key written by Grepdesk.
- `Grepdesk.Tests/InstallerTests.cs` verifies that every key in `AllKeyPaths` has a matching `Root: HKCU; Subkey: "..."; Flags: dontcreatekey uninsdeletekey` entry in `setup.iss`, and that `[UninstallRun]` invokes `Grepdesk.UI.exe --unregister`.
- **If you add, rename, or remove a shell verb or registry key in `WindowsShellIntegration.cs`, update `setup.iss` in the same change.**

### 4.3 Single Source of Truth for Version
- The product version is defined **only** in `Directory.Build.props` (`<Version>`).
- Never add `<Version>` tags to individual `.csproj` files or hardcode version strings in `setup.iss`.

### 4.4 Data Safety & Non-Destructive Defaults
- **Recycle Bin First**: Deleting user files from Browse, Search, Organize, Old Downloads, or Large Old Files must use `IPlatformShell.MoveToTrash` after user confirmation—never permanent `File.Delete` (permanent deletion is reserved for rebuildable caches and temp files in `CleanupExecutor`).
- **No Silent Overwrites**:
  - `TransferEngine` and `ZipExtractor` must check `ConflictPolicy.LooksIdentical` and ask `JobContext.Resolve(conflict)` when destination data would be lost.
  - `FolderOrganizer` never overwrites existing files when moving groups into subfolders and supports full undo.
- **Zip Slip Protection**: Any changes to `ZipExtractor` must preserve path-traversal validation ensuring extracted paths stay strictly inside the destination directory.
- **Lossless Document & Text Editing**:
  - `DocxEditDocument` must only rewrite modified XML parts (`_dirtyParts`) and keep unrecognized XML elements (`NodePiece`) and package entries untouched.
  - `ViewerFile` text editing must preserve the file's original encoding (UTF-8 with/without BOM, UTF-16, or code page) and line endings (`CRLF` vs `LF`).

### 4.5 Performance & Concurrency Discipline
- **Hot-Path Allocations**: `FileIndex`, `DiskAnalyzer`, and `CleanupAnalyzer` iterate over millions of entries on whole-PC scans. Avoid LINQ chains or string allocations per entry on hot paths; prefer `ReadOnlySpan<char>`, `Dictionary.GetAlternateLookup<ReadOnlySpan<char>>()`, and `ArrayPool<byte>.Shared`.
- **Hardware-Aware Parallelism**: Always use `WorkerPolicy.FileOperations(...)` and `WorkerPolicy.Compression(...)` (`Grepdesk.Core/DriveKind.cs`) to determine worker counts. Never run parallel I/O workers on `DriveKind.Rotational` (HDD) or `DriveKind.Removable` (USB sticks), where parallel I/O causes thrashing.
- **Cloud Placeholders & Reparse Points**:
  - Skip traversing directory symlinks/junctions (`FileAttributes.ReparsePoint` with `LinkTarget != null`) to prevent cycles and double counting.
  - Exclude online-only cloud files (`RecallOnDataAccess = 0x00400000` or `FileAttributes.Offline`) from physical disk usage and folder size totals.

### 4.6 Error Logging & Crash Trail
- Many UI event handlers are `async void`. Wrap new top-level async flows carefully or let `CrashHandling` catch and log them via `ErrorLog.Write`.
- Record significant user/window lifecycle milestones with `SessionTrail.Note($"...")` so native crashes (PDFium, Media Foundation, graphics drivers) remain diagnosable on the next startup.

---

## 5. Quick File Map

| Area | Key Files |
| :--- | :--- |
| **Entry Point & IPC** | `Grepdesk.UI/Program.cs`, `Startup/AppCommand.cs`, `Startup/SingleInstanceHost.cs`, `Startup/CommandDispatcher.cs`, `Startup/JobDispatcher.cs` |
| **Settings & Localization** | `Grepdesk.UI/AppSettings.cs`, `LocalizationService.cs`, `Assets/Lang/en.json`, `Assets/Lang/tr.json` |
| **Crash & Error Logging** | `Grepdesk.UI/CrashHandling.cs`, `ErrorLog.cs`, `SessionTrail.cs` |
| **Folder Browser** | `Grepdesk.Core/Browse/FolderListing.cs`, `AddressResolver.cs`, `NavigationHistory.cs`, `FolderBookmarks.cs`, `BrowseColumnWidths.cs`, `FolderViewMode.cs`, `ShortcutFile.cs`, `UndoHistory.cs`, `Grepdesk.UI/Browse/BrowseView.axaml.cs`, `BrowseView.DragDrop.cs`, `BrowseView.ViewModes.cs` |
| **Name & Content Search** | `Grepdesk.Core/FileIndex.cs`, `ContentSearch/ContentSearcher.cs`, `ContentSearch/ExtractorRegistry.cs`, `ContentSearch/PptxExtractor.cs`, `Grepdesk.UI/MainWindow.axaml.cs` |
| **Archive Engine (Zip)** | `Grepdesk.Core/Archive/ZipCompressor.cs`, `ZipExtractor.cs`, `ZipWriter.cs` |
| **Transfer Engine (Copy/Move)** | `Grepdesk.Core/Transfer/TransferEngine.cs`, `IFileCopier.cs`, `Grepdesk.Windows/CopyFile2Copier.cs` |
| **Jobs & Conflict UI** | `Grepdesk.Core/Jobs/JobContext.cs`, `Conflicts.cs`, `Grepdesk.UI/Jobs/JobWindow.axaml.cs`, `JobSpecs.cs` |
| **Disk Usage** | `Grepdesk.Core/DiskUsage/DiskAnalyzer.cs`, `DiskClassifier.cs`, `Grepdesk.UI/DiskUsage/DiskUsageView.axaml.cs` |
| **Cleanup & Duplicates** | `Grepdesk.Core/Cleanup/CleanupAnalyzer.cs`, `CleanupExecutor.cs`, `DuplicateFinder.cs`, `Grepdesk.UI/Cleanup/CleanupView.axaml.cs` |
| **Smart Organize** | `Grepdesk.Core/Grouping/FolderGrouper.cs`, `FolderOrganizer.cs`, `Grepdesk.UI/Organize/OrganizeView.axaml.cs` |
| **Viewer & Editors** | `Grepdesk.Core/Viewer/DocxDocument.cs`, `DocxEditDocument.cs`, `XlsxDocument.cs`, `PptxDocument.cs`, `PdfRenderer.cs`, `PdfPageText.cs`, `SiblingFiles.cs`, `Grepdesk.UI/Viewer/FileViewerWindow.axaml.cs`, `DocxEditor.cs`, `XlsxView.cs`, `PptxView.cs` |
| **Standalone CLI** | `Grepdesk.Cli/Program.cs`, `CliRunner.cs`, `Commands/*` |
| **Platform Implementations** | `Grepdesk.UI/Helpers/PlatformShellFactory.cs`, `Grepdesk.Windows/*`, `Grepdesk.Linux/*`, `Grepdesk.MacOS/*` |
| **Build & Packaging** | `Directory.Build.props`, `build.py`, `build.ps1`, `setup.iss` |
| **Further Documentation** | `docs/README.md`, `docs/ARCHITECTURE.md`, `docs/USER_GUIDE.md`, `docs/BUILD.md`, `docs/CLI.md`, `docs/ROADMAP.md` |
