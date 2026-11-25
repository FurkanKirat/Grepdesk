# Grepdesk Internal Build & Bugfix Log

This document maps each `<InternalBuildNumber>` (`#1`, `#2`, `...`) in [`Directory.Build.props`](../Directory.Build.props) to the exact log signatures, root causes, and fixes it introduced, so future log inspections immediately show whether a crash or hang is already resolved or has regressed.

---

## How to Use This Log

- Every build stamps its internal build number as `#N` (e.g., `Grepdesk 0.2.0 #2 (commit)`) into:
  - Daily error logs (`%LOCALAPPDATA%\Grepdesk\logs\grepdesk-YYYY-MM-DD.log`)
  - Session trail headers (`session-<pid>.trail`)
  - **Settings → About**
- **Triage rule** when inspecting `%LOCALAPPDATA%\Grepdesk\logs`:
  1. **No `#N` or `#K < #N`** (where the bug was fixed in `#N`): pre-fix log entry — **not** a regression / false alarm.
  2. **`#K >= #N` with the same signature**: the bug is **still occurring or has regressed** — reopen it, investigate, fix it, increment `<InternalBuildNumber>`, and prepend a new entry here.
- **Maintenance rule**: Whenever `<InternalBuildNumber>` is incremented in `Directory.Build.props`, add a row to the Summary Table and prepend a detailed `#N` section below.

---

## Summary Table

| Build | Date | Log Signature / Symptom | Fix | Status |
| :--- | :--- | :--- | :--- | :--- |
| **`#2`** | 2026-11-25 | `EventID=1002` (`Application Hang`) in `FileViewerWindow` Edit mode (`viewer: edit on` / `viewer: rendered off`) when selecting the last characters of a line and pasting (`#1`: `grepdesk-2026-11-25.log`, `session-46716.trail`). | Clamped `TextView.MakeVisible` in `GrepdeskTextEditor`, replaced built-in `OnPaste` with `PasteIntoEditorAsync()`, and fixed CRLF Markdown folding offsets. | Fixed in `#2` — verify no `#2`+ hangs |
| **`#1`** | 2026-11-25 | `0xc0000005` in `SkiaSharp.SKStream.Read` ← `GlyphTypefaceImpl.TryGetStream` ← `TryCreateSyntheticGlyphTypeface` (`grepdesk-2026-11-22.log`, `d643e4f`). | Upgraded `Avalonia` packages from `11.3.22` to `11.3.23` (upstream `#22323` / PR `#22757`). | Fixed in `#1` — verify no `#1`+ occurrences |
| **`#1`** | 2026-11-25 | `EventID=1002` (`Application Hang`) opening `.rs` / `.md` in viewer (`grepdesk-2026-11-24.log` & `grepdesk-2026-11-25.log`, `f0d8b8a`). | Set `AllowScrollBelowDocument = true` (vertical `#614` workaround) and added `250 ms` `RegexTimeout` + `SafeMatch` in `CodeSymbolExtractor`. | Reopened on `#1` (horizontal/paste hang) → fixed in `#2` |
| **`Pre-#1`** | 2026-11-19 | PDFium `0xc0000409` fail fast while scrolling PDFs & `0xc0000005` in `DocumentWrapper.Dispose()` (`2026-10-27`..`2026-11-19`, `46c6825`/`d40a7a6`/`d643e4f`). | Added `FormInfoPinning` in `Grepdesk.Core/Viewer/PdfRenderer.cs` (commit `58de1d5`). | Fixed pre-`#1` |

---

## Detailed Entries

### Build `#2` (`0.2.0 #2`, 2026-11-25)

#### Viewer Edit Mode Hang on End-of-Line Selection & Paste
- **Symptom / Log Signature**:
  - `Windows: Grepdesk.UI.exe hung (Application Hang, type Unknown, ...)` (`EventID=1002`)
  - Trail ends in Edit mode (`viewer: edit on` / `viewer: rendered off`) when selecting the last characters of a line and pasting (`grepdesk-2026-11-25.log` PID `32968` and `session-46716.trail` PID `46716`, both on `#1`).
- **Root Cause**:
  1. **AvaloniaEdit `#606` / `#609` (`11.4.1`)**: `Caret.BringCaretToView(5.0)` inflates the caret rectangle by `5.0 px`, while `TextView.MakeVisible(Rect)` does not clamp against `_scrollExtent` / `_scrollViewport`. Selecting to the end of a line overshoots the scroll extent; `ArrangeOverride` clamps back and raises `ScrollOffsetChanged`, which triggers `SelectionMouseHandler.TextView_ScrollOffsetChanged` → `ExtendSelectionToMouse` → `BringCaretToView(5.0)` → `MakeVisible`, while `AvaloniaEdit.TextMate` posts `TokenizeViewPort` at `DispatcherPriority.Default` every cycle.
  2. **Broken `EditingCommandHandler.OnPaste` (`11.4.1`)**: Calls `Document.BeginUpdate()` before `await Clipboard.GetTextAsync()` and `Caret.BringCaretToView()` before `Document.EndUpdate()`, triggering layout/TextMate updates before `Document.UpdateFinished` fires.
  3. **CRLF-Splitting Markdown Foldings**: `CodeFolder.CollectMarkdownFoldings` used `FindLineEnd` (`\n` index) for `HeaderEndOffset` and `prevLineEnd`, placing folding boundaries between `\r` and `\n` on Windows CRLF files.
- **Fix**:
  - Added `GrepdeskTextEditor`, `GrepdeskTextArea`, and `GrepdeskTextView` in [`Grepdesk.UI/Viewer/GrepdeskTextEditor.cs`](../Grepdesk.UI/Viewer/GrepdeskTextEditor.cs) overriding `MakeVisible` with `ClampToScrollBounds(...)`, and switched `Editor` in [`Grepdesk.UI/Viewer/FileViewerWindow.axaml`](../Grepdesk.UI/Viewer/FileViewerWindow.axaml) to `<viewer:GrepdeskTextEditor>`.
  - Intercepted `Ctrl+V` and `Shift+Insert` in `FileViewerWindow.OnPreviewKeyDown` (when `Editor.IsKeyboardFocusWithin`) with `PasteIntoEditorAsync()` in [`Grepdesk.UI/Viewer/FileViewerWindow.axaml.cs`](../Grepdesk.UI/Viewer/FileViewerWindow.axaml.cs), awaiting `Clipboard.TryGetTextAsync()` before `using (doc.RunUpdate())` and calling `BringCaretToView()` after `RunUpdate()` completes.
  - Updated `CodeFolder.CollectMarkdownFoldings` in [`Grepdesk.Core/Viewer/CodeEditorTools.cs`](../Grepdesk.Core/Viewer/CodeEditorTools.cs) to use `contentEnd` (excluding `\r`).
- **Regression Check**:
  - Any `EventID=1002` (`Application Hang`) on `#2` or later during viewer Edit mode selection or paste means this issue is not yet fully resolved.

---

### Build `#1` (`0.2.0 #1`, 2026-11-25)

#### 1a. Avalonia `SKStream.Read` / `TryCreateSyntheticGlyphTypeface` Access Violation
- **Symptom / Log Signature**:
  - `exception 0xc0000005` in `coreclr.dll` with managed stack `SkiaSharp.SKStream.Read` ← `Avalonia.Skia.GlyphTypefaceImpl.TryGetStream` ← `FontCollectionBase.TryCreateSyntheticGlyphTypeface` ← `TextBlock.CreateTextLayout` (`grepdesk-2026-11-22.log`, 2 occurrences on `d643e4f`).
- **Root Cause**:
  - Upstream Avalonia issue `#22323` (use-after-free where `SKStreamAsset` is finalized while `sk_stream_read` copies font data for synthetic bold/oblique).
- **Fix**:
  - Upgraded `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, and `Avalonia.Fonts.Inter` from `11.3.22` to `11.3.23` (includes PR `#22757`) in [`Grepdesk.UI/Grepdesk.UI.csproj`](../Grepdesk.UI/Grepdesk.UI.csproj).
- **Regression Check**:
  - Any `SKStream.Read` / `GlyphTypefaceImpl.TryGetStream` crash on `#1`+ indicates the Avalonia `11.3.23` fix did not cover the case.

#### 1b. Viewer Editor Hang & Symbol Regex Protection *(partially reopened on `#1`, fixed in `#2`)*
- **Symptom / Log Signature**:
  - `EventID=1002` (`Application Hang`) after opening `.rs` or `.md` in the viewer (`grepdesk-2026-11-24.log` PID `21512`, `grepdesk-2026-11-25.log` PIDs `28560`, `47368`, all on `f0d8b8a`).
- **Root Cause & Partial Fix**:
  - Enabled `Editor.Options.AllowScrollBelowDocument = true` in [`Grepdesk.UI/Viewer/FileViewerWindow.axaml.cs`](../Grepdesk.UI/Viewer/FileViewerWindow.axaml.cs) (vertical `#614` workaround) and added a `250 ms` `RegexTimeout` + `SafeMatch` to `CodeSymbolExtractor` in [`Grepdesk.Core/Viewer/CodeEditorTools.cs`](../Grepdesk.Core/Viewer/CodeEditorTools.cs).
  - Horizontal/paste hangs still occurred on `#1` (`grepdesk-2026-11-25.log` PID `32968`, `session-46716.trail`) and were addressed in **Build `#2`**.

#### 1c. Diagnostics & False-Alarm Suppression
- Added `<InternalBuildNumber>` in [`Directory.Build.props`](../Directory.Build.props) and `AppInfo.InternalBuild` in [`Grepdesk.UI/AppInfo.cs`](../Grepdesk.UI/AppInfo.cs).
- Persisted `#N` and `AppDisplay` in `SessionTrail` headers and added Windows `EventID=1002` (`Application Hang`) parsing in [`Grepdesk.UI/SessionTrail.cs`](../Grepdesk.UI/SessionTrail.cs).
- Updated `CrashHandling.ReportPreviousCrashes` in [`Grepdesk.UI/CrashHandling.cs`](../Grepdesk.UI/CrashHandling.cs) to tag older-build crashes in the log and suppress modal crash popups for older builds.
- Added `viewer: edit on/off` and `viewer: rendered on/off` trail notes in [`Grepdesk.UI/Viewer/FileViewerWindow.axaml.cs`](../Grepdesk.UI/Viewer/FileViewerWindow.axaml.cs).

---

### Pre-`#1` (`0.2.0 (58de1d5)`, 2026-11-19)

#### PDFium Native Crashes (`0xc0000409` / `0xc0000005`)
- **Symptom / Log Signature**:
  - `exception 0xc0000409 (fail fast / stack buffer overrun)` while scrolling PDFs (`grepdesk-2026-10-27.log`, `grepdesk-2026-11-10.log`, `grepdesk-2026-11-12.log` on `46c6825` / `d40a7a6`).
  - `exception 0xc0000005` in `Docnet.Core.Bindings.DocumentWrapper.Dispose()` when closing the PDF viewer (`grepdesk-2026-11-19.log` on `d643e4f`).
- **Root Cause**:
  - `Docnet 2.6.0` passed an unpinned managed `FPDF_FORMFILLINFO` struct to PDFium's `FPDFDOCInitFormFillEnvironment`, allowing GC compaction to move it before `DocumentWrapper.Dispose()`.
- **Fix**:
  - Added `FormInfoPinning` in [`Grepdesk.Core/Viewer/PdfRenderer.cs`](../Grepdesk.Core/Viewer/PdfRenderer.cs) (commit `58de1d5`).
