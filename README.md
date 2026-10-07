# Grepdesk

[![Latest release](https://img.shields.io/github/v/release/FurkanKirat/Grepdesk)](https://github.com/FurkanKirat/Grepdesk/releases/latest)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**The file jobs Windows Explorer is slow at, in one fast app:** find files by
name or by what's inside them, see what fills your disk and get the space
back, tidy up Downloads, and zip, unzip, copy or move big folders, right from
Explorer's right-click menu.

<!-- SCREENSHOT: a 10–15 s GIF here (type in the search box, results appear, Space opens the viewer),
     saved as docs/images/demo.gif, then:
![Grepdesk](docs/images/demo.gif)
-->

**[Download for Windows](https://github.com/FurkanKirat/Grepdesk/releases/latest)** ·
[Features](#features) · [Benchmarks](#benchmarks) · [Build from source](#build-from-source)

## Download

Get `Grepdesk-vX.Y.Z-win-x64-Setup.exe` from the
[latest release](https://github.com/FurkanKirat/Grepdesk/releases/latest), or
the portable zip if you'd rather not install anything. Windows 10 or 11, 64-bit;
.NET is included, nothing else to install. English and Turkish.

The app isn't code-signed yet, so on first launch Windows SmartScreen may say
it "protected your PC": click **More info → Run anyway**. Every release is
built from this repository, and you can [build it yourself](#build-from-source).

## How fast

Times from the [benchmarks](#benchmarks) below (lower is better):

| | Grepdesk | Explorer | 7-Zip / robocopy |
| --- | ---: | ---: | ---: |
| Zip 10,000 source files (148 MB) | **0.56 s** | 15.59 s | 1.65 s |
| Unzip them | **4.04 s** | 130.61 s | 13.25 s |
| Unzip 1 GB of photos and videos | **0.38 s** | 12.24 s | 1.03 s |
| Copy 10,000 files on the same SSD | **2.88 s** | 75.93 s | 4.35 s (robocopy) |

Zip jobs run on every core at once, and already-compressed files are stored
instead of compressed again. Copies start right away instead of "calculating"
first, with many files in flight on SSDs and one at a time on hard disks.

## Why Grepdesk

There are excellent single-purpose tools for each of these jobs:
[Everything](https://www.voidtools.com/) for name search,
[WizTree](https://diskanalyzer.com/) for disk usage, [7-Zip](https://www.7-zip.org/)
for archives, [TeraCopy](https://www.codesector.com/teracopy) for copying. If you
already live in them, keep them: they are more mature than Grepdesk in their
own lane.

Grepdesk puts those jobs in one open-source app that looks and behaves the same
throughout, and connects them: search *inside* files as well as by name, then
open, preview, zip or send a result to your editor or terminal from the same
list; see what fills a drive and clean it up on the next page; paste a big
copy from Explorer and get a fast, pausable job with sensible conflict handling.
Free, no ads, no telemetry; the only network request is the optional update check.

## Features

### 🔍 File Name Search
- Index a specific folder (or several) or your whole PC — or drop a folder on the window
- Instant, debounced search as you type; the matched part of each name is highlighted
- Leave the box empty to browse everything that was scanned
- Size and date for every result, folder sizes included (total of everything inside)
- Sort by name, size or date across **all** matches, not just the first page
- Filter by type (folders, documents, images, code, archives, video, audio), minimum size and last modified
- Preview panel: images, rendered Markdown, the start of text and code files, the text of Office documents and PDFs; on Windows also video frames, album art and the first slide of presentations, with duration, resolution and similar details
- Viewer window for text, code and Markdown files (Space): the whole file, read-only, with line numbers and Ctrl+F

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

### 🗂️ Organize
Make sense of a messy folder (Downloads, typically) without reading a single
file's contents:
- **Smart** — first series (names that differ only by a number, date, id or
  copy marker: `dracula_idle_1.png … dracula_idle_37.png`, `ChatGPT Image 24 May
  2025…`), then families sharing their first words (`Lecture 1 - Intro.pdf`,
  `Lecture 2 - Sorting.pdf`), then whatever is left by type
- **Source** — the site each download came from (read from the
  Zone.Identifier stream browsers attach on Windows)
- **Type**, and download **sessions** (files that arrived within 30 minutes)
- **Leftovers** — archives whose extracted folder sits next to them, and
  `name (1).ext` re-downloads of the same size
- Move a group into a subfolder (never overwrites, one-click undo) or to the
  Recycle Bin. Open it from the sidebar, a folder's right-click menu, or the
  Old downloads card on Free Up Space

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
- **View** (`Space`) — text, code and Markdown files in a read-only viewer window
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
language (English / Türkçe, or follow the system), the preview panel toggle,
and an optional update check (off by default) that tells you when a new
release is out on GitHub.

## Benchmarks

Seconds, lower is better; the fastest in each row is in bold.

**Compress**

| Data | Grepdesk | 7-Zip | .NET, 1 thread | Explorer |
| --- | ---: | ---: | ---: | ---: |
| 10,000 source files, 148 MB | **0.56 s** | 1.65 s | 6.52 s | 15.59 s |
| 306 photos, videos and notes, 986 MB | **0.66 s** | 2.91 s | 36.27 s | 36.02 s |
| 2 files (a log and a disk image), 1 GB | **1.87 s** | 87.58 s | 42.54 s | 32.16 s |

**Extract**

| Data | Grepdesk | 7-Zip | .NET, 1 thread | Explorer |
| --- | ---: | ---: | ---: | ---: |
| 10,000 source files, 148 MB | **4.04 s** | 13.25 s | 9.43 s | 130.61 s |
| 306 photos, videos and notes, 986 MB | **0.38 s** | 1.03 s | 1.63 s | 12.24 s |
| 2 files (a log and a disk image), 1 GB | **1.38 s** | 2.45 s | 1.40 s | 8.56 s |

**Copy, same drive**

| Data | Grepdesk | robocopy /MT:8 | .NET, 1 thread | Explorer |
| --- | ---: | ---: | ---: | ---: |
| 10,000 source files, 148 MB | **2.88 s** | 4.35 s | 10.08 s | 75.93 s |
| 306 photos, videos and notes, 986 MB | **0.30 s** | 0.34 s | 0.72 s | 2.31 s |
| 2 files (a log and a disk image), 1 GB | 1.04 s | **0.26 s** | 0.33 s | 0.47 s |

**Copy to another drive**

| Data | Grepdesk | robocopy /MT:8 | .NET, 1 thread | Explorer |
| --- | ---: | ---: | ---: | ---: |
| 10,000 source files, 148 MB | 10.24 s | **1.35 s** | 10.15 s | 62.86 s |
| 306 photos, videos and notes, 986 MB | 0.42 s | **0.25 s** | 0.61 s | 2.26 s |
| 2 files (a log and a disk image), 1 GB | 0.30 s | **0.21 s** | 0.32 s | 0.65 s |

Where it isn't the fastest yet:

- **Many small files to another drive**: robocopy is about 7× faster. Grepdesk
  copies those at the speed of a single thread, which is being looked into.
- **Big files on the same drive**: files over 256 MB are copied around the
  file cache, so a large copy doesn't push everything else out of memory.
  With a warm cache, as in these runs, that costs time against tools that use it.
- **Media compression**: photos and videos are already compressed, so Grepdesk
  stores them as they are; 7-Zip and Explorer try to compress them again. That
  is a real saving, but also why that row's gap is so wide.

Measured on Windows 11, Intel Core i7-13650HX (20 threads), NVMe SSDs
(Samsung 980 PRO → WD SN740 for the other-drive rows), .NET 10.0.12, with a
warm file cache. The datasets are generated, so the runs can be reproduced
(see below).

### How it's measured

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

## Build from source

```bash
git clone https://github.com/FurkanKirat/Grepdesk.git
cd Grepdesk
dotnet build
dotnet run --project Grepdesk.UI
dotnet test
```

### Building the installer

```bash
dotnet publish Grepdesk.UI -p:PublishProfile=win-x64
```

writes a self-contained build (the .NET runtime included, so users don't need
it installed) to `publish/win-x64`. Then compile `setup.iss` with
[Inno Setup](https://jrsoftware.org/isinfo.php) 6; the installer lands in `Output/`.

Timing a single job from a script: add `--benchmark` (e.g. `Grepdesk.UI.exe --extract-to --benchmark a.zip`);
the elapsed time is appended to `%LOCALAPPDATA%\Grepdesk\benchmark.log`.

## Under the hood

- **.NET** / C#
- **Avalonia UI** — cross-platform UI framework
- Platform-specific shell integration behind `IPlatformShell`,
  `IShellIntegration`, `IFileClipboard`, `IDriveKindProvider` and `IFileCopier`
  (Windows and Linux implemented; macOS partly, see below)

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

## Linux and macOS

**Grepdesk is tested on Windows 10 and 11.** The Linux and macOS builds compile
and the test suite passes on Linux, but the app itself hasn't been tried there
yet; reports are welcome.

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

[MIT](LICENSE). Copyright (c) 2026 Furkan Kırat.
