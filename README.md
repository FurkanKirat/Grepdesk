# Grepdesk

A fast, lightweight desktop tool built with Avalonia UI for the file jobs
Windows Explorer is slow at: finding files (by name or by content), zipping
and unzipping, and copying or moving large folders.

## Features

### 🔍 File Name Search
- Index a specific folder (or several) or your whole PC — or drop a folder on the window
- Instant, debounced search as you type; the matched part of each name is highlighted
- Leave the box empty to browse everything that was scanned
- Size and date for every result, folder sizes included (total of everything inside)
- Sort by name, size or date across **all** matches, not just the first page
- Filter by type (folders, documents, images, code, archives, video, audio), minimum size and last modified
- Preview panel: images, the start of text and code files, the text of Office documents and PDFs

### 📄 Content Search
- Search inside file contents — not just names
- Supports plain text and code files out of the box (`.txt`, `.md`, `.cs`,
  `.py`, `.js`, `.json`, `.xml`, `.html`, `.csv`, `.yaml`, and more)
- Shows a matching snippet alongside each result
- Skips unreadable files (locked, corrupt, unsupported) without stopping the scan

### 📊 Disk Usage
- Pick a drive and see what fills it: games, developer tools (incl. local AI
  models and package caches), apps, system and caches, videos, pictures,
  documents, music
- Where a file lives decides its kind first (a video inside a game folder counts
  as the game), then its extension
- Games, apps and developer folders are listed as whole items (each game with
  all its files added up, like Windows' Installed apps page); videos, pictures,
  documents and music as their largest files
- Drill into the largest folders from the drive root
- Space the scan can't read (other users, System Volume Information, restore
  points) and online-only cloud files are reported separately instead of hidden
- Reuses an existing whole-PC scan from the name search page

### 🧹 Free Up Space
Every way to get space back on one page, each with what it is, whether it can
be undone, and the items to pick from. Nothing is deleted until you select it
and confirm.
- **Recycle Bin** — deleted files keep using space until it is emptied
- **Caches** — browser, Electron app and GPU shader caches (deleted for good;
  apps rebuild them). Web apps' offline storage is listed but not pre-selected
- **Developer caches** — npm, pip, NuGet, Yarn, Gradle caches and `node_modules`
  folders untouched for 90 days
- **Temporary files** older than a day
- **Duplicate files** — compared by size, then a 64 KB head/tail sample, then a
  full XxHash128, so most files are never read in full; the oldest copy is kept
- **Old downloads** and **large, old files** — listed, never pre-selected, moved
  to the Recycle Bin
- **Hibernation file** and **Windows' own cleanup** (Disk Cleanup, Storage
  settings, restore points) for what needs administrator rights

### 🗜️ Fast Zip
- **Extract here / Extract to folder** on any `.zip` from Explorer's right-click menu
- **Compress with Grepdesk** on any files or folders
- Both run in parallel across files; already-compressed files (photos,
  videos, archives, Office documents) are stored instead of re-compressed
- Zip64 (files over 4 GB, more than 65,535 entries) and UTF-8 file names
- Protects against malicious archives that try to write outside the target folder

### 📋 Fast Copy & Move
- Copy (`Ctrl+C`) or cut (`Ctrl+X`) in Explorer as usual, then right-click
  the destination and choose **Paste with Grepdesk**
- Starts copying immediately instead of "calculating" first
- Copies several files at once on SSDs and network shares, one at a time on
  spinning disks and USB sticks (where parallel I/O is slower)
- A move on the same drive is an instant rename

All jobs show progress, speed and time left, can be paused or cancelled, and
only ask about a conflict when overwriting would actually lose data.

### 🖱️ Explorer Integration
Tick the features you want on the **Settings** page (bottom of the sidebar). Entries are written to the current user's registry (no admin rights)
and follow the exe if it moves. On Windows 11 they appear under
**Show more options**. Selecting many files launches one process per file;
Grepdesk merges them into a single job.

### ⚡ Quick Actions (right-click any result)
- **Open** (`Enter`) — launch with the default associated app
- **Show in File Manager** (`Ctrl+Enter`) — reveal and select the file in Explorer
- **Copy Path** (`Ctrl+C`) / **Copy file** (`Ctrl+Shift+C`) — the path as text, or the file itself to paste in Explorer
- **Compress to zip**, and **Extract here / to folder** on `.zip` files
- **Move to Recycle Bin** (`Delete`, asks first)
- Drag a result out to Explorer, an editor or a mail (always copied, never moved)
- **Open in Terminal** — open a terminal at the file's folder
- **Open in Editor** — automatically detects installed editors (VS Code,
  Rider, Notepad++, ...) on your `PATH` and lists only the ones compatible
  with the selected item (e.g. Notepad++ only shows up for files, not folders)

All keyboard shortcuts are listed on the **Settings** page, along with the
language (English / Türkçe, or follow the system) and the preview panel toggle.

## Why

Most file search tools stop at file names. Grepdesk adds a second mode for
searching *inside* files, plus the small quality-of-life actions (terminal,
editor, explorer) that turn "found it" into "now I can actually use it" —
without leaving the app.

## Tech Stack

- **.NET** / C#
- **Avalonia UI** — cross-platform UI framework
- Platform-specific shell integration behind `IPlatformShell`,
  `IShellIntegration`, `IFileClipboard`, `IDriveKindProvider` and `IFileCopier`
  (Windows and Linux implemented; macOS partly, see below)

## Architecture Notes

- `FileIndex` — in-memory file name index, built once per scan
- `ContentSearcher` + `ExtractorRegistry` — pluggable content extraction per
  file extension
- `IPlatformShell` — abstracts OS-specific actions (open file, show in file
  manager, open terminal, open in editor) behind a single interface, so the
  UI layer never touches `Process.Start` directly
- `ZipExtractor`, `ZipCompressor`, `ZipWriter` — parallel zip engines; the
  writer implements the container format itself so compressed entries from
  many workers can be appended as they finish
- `TransferEngine` — copy/move with a walker feeding workers through a
  bounded channel; the byte copy is `CopyFile2` on Windows (`IFileCopier`)
- `SingleInstanceHost` + `JobDispatcher` — mutex + named pipe so the
  per-file processes Explorer starts end up as one job window
- `EditorDetector` — detects installed editors on `PATH` at startup and
  caches the result
- `EditorTargetResolver` — resolves whether an editor expects a file or a
  directory, and filters incompatible combinations before they ever reach
  the shell layer
- Every shell action returns a `ShellActionResult` (success/failure +
  exception), so the UI can report *why* something failed instead of
  silently swallowing errors

## Status

Actively developed as a personal tool / learning project. Windows is the
primary target, Linux is supported, macOS lacks Finder integration.

## Getting Started

```bash
git clone https://github.com/FurkanKirat/Grepdesk.git
cd Grepdesk
dotnet build
dotnet run --project Grepdesk.UI
dotnet test
```

For daily use, publish once and point the Explorer menu at the published exe
(ReadyToRun roughly halves the startup time of each job window):

```bash
dotnet publish Grepdesk.UI -c Release -r win-x64 --self-contained false -o C:\Apps\Grepdesk
```

Timing a single job from a script: add `--benchmark` (e.g. `Grepdesk.UI.exe --extract-to --benchmark a.zip`);
the elapsed time is appended to `%LOCALAPPDATA%\Grepdesk\benchmark.log`.

## Benchmarks

`Grepdesk.Benchmarks` times Grepdesk's engines against Windows Explorer's own
zip and copy engines (driven through COM), 7-Zip, robocopy and single-threaded
.NET, on three generated datasets (many small source files, photos/videos,
a few large files). Every run's output is checked for completeness before its
time counts, and a results table is saved as Markdown.

```bash
dotnet run -c Release --project Grepdesk.Benchmarks                                # everything, ~30 min
dotnet run -c Release --project Grepdesk.Benchmarks -- --scale 0.1 --rounds 1      # quick smoke run
dotnet run -c Release --project Grepdesk.Benchmarks -- --other D:\bench --ops copy # copy to another drive
```

Options: `--work`, `--other`, `--scale`, `--rounds`, `--warmup`, `--datasets`,
`--ops`, `--only` (see the top of `Grepdesk.Benchmarks/Program.cs`).
Times are with a warm file cache: clearing it needs admin rights, so for cold
numbers run once right after a reboot.

Performance guard tests (Grepdesk must stay clearly faster than
single-threaded .NET and than Explorer) are skipped unless enabled:

```powershell
$env:GREPDESK_PERF = "1"; dotnet test -c Release --filter Category=Performance
```

## Linux and macOS

The engines (zip, copy/move, conflicts) are plain .NET and run everywhere.
Platform pieces:

| | Windows | Linux | macOS |
| --- | --- | --- | --- |
| File manager menu | Explorer (registry) | Dolphin, Nautilus (under *Scripts*), Nemo | not yet: Finder needs a signed extension |
| Paste from clipboard | yes | yes, with `wl-clipboard` (Wayland) or `xclip` (X11) | not yet |
| SSD / HDD detection | yes | yes (`/sys` + `/proc/self/mountinfo`) | yes (`diskutil`), untested |
| Native fast copy | `CopyFile2` | `copy_file_range` / reflinks via `File.Copy` | `clonefile` via `File.Copy` |

Testing on Linux from Windows (needs Docker):

```powershell
docker run --rm -v "${PWD}:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c `
  "cp -r /src /work && cd /work && find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + && dotnet test Grepdesk.Tests"
```

## License
MIT License

Copyright (c) 2026 Furkan Kırat

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
