# Grepdesk

A fast, lightweight desktop search tool built with Avalonia UI. Grepdesk indexes
your files for instant name-based search, and separately lets you search
*inside* file contents (text, code, docs) without needing a pre-built index.

## Features

### 🔍 File Name Search
- Index a specific folder (or several) or your whole PC
- Instant, debounced search as you type
- Keyboard navigation (`↓` to jump into results, `Esc` to clear)

### 📄 Content Search
- Search inside file contents — not just names
- Supports plain text and code files out of the box (`.txt`, `.md`, `.cs`,
  `.py`, `.js`, `.json`, `.xml`, `.html`, `.csv`, `.yaml`, and more)
- Shows a matching snippet alongside each result
- Skips unreadable files (locked, corrupt, unsupported) without stopping the scan

### ⚡ Quick Actions (right-click any result)
- **Open** — launch with the default associated app
- **Show in File Manager** — reveal and select the file in Explorer
- **Copy Path** — copy the full path to clipboard
- **Open in Terminal** — open a terminal at the file's folder
- **Open in Editor** — automatically detects installed editors (VS Code,
  Rider, Notepad++, ...) on your `PATH` and lists only the ones compatible
  with the selected item (e.g. Notepad++ only shows up for files, not folders)

## Why

Most file search tools stop at file names. Grepdesk adds a second mode for
searching *inside* files, plus the small quality-of-life actions (terminal,
editor, explorer) that turn "found it" into "now I can actually use it" —
without leaving the app.

## Tech Stack

- **.NET** / C#
- **Avalonia UI** — cross-platform UI framework
- Platform-specific shell integration behind an `IPlatformShell` abstraction
  (Windows implemented; Linux/macOS scaffolded)

## Architecture Notes

- `FileIndex` — in-memory file name index, built once per scan
- `ContentSearcher` + `ExtractorRegistry` — pluggable content extraction per
  file extension
- `IPlatformShell` — abstracts OS-specific actions (open file, show in file
  manager, open terminal, open in editor) behind a single interface, so the
  UI layer never touches `Process.Start` directly
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
primary target; the shell abstraction is designed to make Linux/macOS
support straightforward to add later.

## Getting Started

```bash
git clone https://github.com/FurkanKirat/Grepdesk.git
cd Grepdesk
dotnet build
dotnet run --project Grepdesk.UI
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
