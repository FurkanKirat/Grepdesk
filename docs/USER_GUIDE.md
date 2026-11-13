# Grepdesk User Guide

Grepdesk combines a fast folder browser, file name & content search, disk usage analyzer, space cleanup utility, smart folder organizer, built-in file viewer/editor, and multi-core zip/copy/move engines in a single desktop application.

---

## 1. Installation & First Launch

### Installer vs. Portable
- **Windows Installer (`Grepdesk-vX.Y.Z-win-x64-Setup.exe`)**: Installs Grepdesk, creates Start Menu and optional Desktop shortcuts, and cleanly removes all Explorer context-menu entries and file associations on uninstall.
- **Portable Archive (`Grepdesk-vX.Y.Z-win-x64-portable.zip`)**: Extract anywhere and run `Grepdesk.UI.exe`. The .NET runtime is bundled; no installation or administrator privileges are required.

> [!NOTE]
> Because Grepdesk is not yet code-signed, Windows SmartScreen may display a prompt on first launch. Click **More info → Run anyway**.

---

## 2. Application Pages

Switch between pages using the left sidebar or keyboard shortcuts (`Ctrl+1` through `Ctrl+6`, and `Ctrl+,` for Settings).

### 📁 1. Files (`Ctrl+1`)
An everyday folder browser designed for speed and keyboard navigation:
- **Navigation**: Back (`Alt+←` or `Backspace`), Forward (`Alt+→`), Up one level (`Alt+↑`), and mouse side buttons (`XButton1` / `XButton2`).
- **Breadcrumb Address Bar**: Click segments to jump up the path, or press `Ctrl+L` to type any path, environment variable (`%APPDATA%`, `%TEMP%`), or `~`.
- **Places Sidebar**: Quick access to user folders (Desktop, Downloads, Documents, Pictures, Music, Videos) and local/removable/network drives with free space indicators.
- **Sorting & Live Filtering**: Sort by **Name** (natural order: `file2` before `file10`), **Date modified**, **Type**, or **Size** (folders always stay grouped above files). Press `Ctrl+F` to filter the current folder as you type, or click the search button next to the filter box to search recursively in all subfolders.
- **Folder Sizes**: Subfolder sizes are calculated automatically up to a safe budget (1 GB or 5,000 entries) so browsing stays fast. Press `Ctrl+Shift+S` (or click **Calculate sizes**) to compute the full size of large folders on demand.
- **File Operations**:
  - **Rename** (`F2`): Edits the name inline with the extension deselected; prevents invalid characters and reserved Windows device names (`CON`, `NUL`, `COM1`, etc.).
  - **New Folder** (`Ctrl+Shift+N`): Creates a folder and immediately starts inline rename.
  - **Drag and drop**: drag files out of the list into Explorer or any other app, or drop files from Explorer, other apps or within Grepdesk onto a folder row, a sidebar place or the open folder. Like Explorer, a drop moves within a drive and copies across drives; hold `Ctrl` to copy or `Shift` to move. Drops run as the same transfer job as paste (conflict prompts, progress, cancel).
  - **Paste a screenshot**: with an image on the clipboard (Print Screen, a snip) and no files, `Ctrl+V` saves it as a PNG in the open folder.
  - **Cut / Copy / Paste** (`Ctrl+X`, `Ctrl+C`, `Ctrl+V`): Uses the native OS file clipboard so you can copy in Explorer and paste in Grepdesk (or vice versa). Pastes run using Grepdesk's multi-threaded transfer engine.
  - **Copy Path** (`Ctrl+Shift+C`): Copies the selected file/folder paths as text.
  - **Move to Recycle Bin** (`Delete`): Confirms before moving selected items to the Recycle Bin.
  - **Hidden Items** (`Ctrl+H`): Toggles visibility of hidden and system files.

---

### 🔍 2. File Name Search (`Ctrl+2`)
Find files and folders across a single directory, dropped folders, or your entire PC:
- **Instant Results**: Debounced search updates as you type and highlights the matched substring in each file name.
- **Browse All**: Leave the search box empty to browse all indexed items (useful with **Sort: Size (largest first)** to spot the biggest files or folders).
- **Folder Sizes Included**: Every folder entry displays the rolled-up total size of all files beneath it.
- **Filters**: Filter by **Category** (*All, Folders, Documents, Images, Code, Archives, Videos, Audio, Other*), **Minimum Size**, and **Modified Date** (*Last 24 hours, Last 7 days, Last 30 days, Last year*).
- **Drag & Drop**:
  - Drop a folder onto the Grepdesk window to index and search it.
  - Drag any search result out of Grepdesk into Explorer, an editor, or an email client (always copies, never moves the original).

---

### 📄 3. Content Search (`Ctrl+3`)
Search inside file contents without needing a pre-built full-text index:
- **Supported Formats**:
  - Plain text, source code, and configuration files (`.txt`, `.md`, `.cs`, `.py`, `.js`, `.ts`, `.json`, `.xml`, `.yaml`, `.html`, `.css`, `.sql`, `.csv`, `.sh`, `.ps1`, `.log`, etc.)
  - **PDF documents** (`.pdf`)
  - **Word documents** (`.docx`)
  - **Excel spreadsheets** (`.xlsx`)
- **Live Streaming**: Matches stream into the list as they are found alongside a surrounding text snippet.
- **Viewer Integration**: Pressing `Space` on a content match opens the built-in viewer and automatically jumps to and highlights the matched query.
- **Transparent Skip Reporting**: Files over 20 MB or files that cannot be decoded (password-protected, corrupt, or locked) are counted and reported in the status bar without interrupting the scan.

---

### 📊 4. Disk Usage (`Ctrl+4`)
Understand what is consuming space on a drive:
- **Location-Aware Classification**: Where a file lives takes precedence over its extension—for example, a video file inside a Steam game folder is counted as part of that **Game**, not as a loose video.
- **Categories**: *Games, Developer tools (including local AI models like Ollama/LM Studio and package caches), Apps, System and caches, Videos, Pictures, Documents, Music and audio, Other*.
- **Whole-Item Grouping**: Games, apps, and developer folders are rolled up as single items (e.g., an entire game folder or `node_modules` tree with its total size and file count), while media and documents list their largest individual files.
- **Folder Drill-Down**: Double-click folders under **Largest folders** to inspect subdirectories, and press `Backspace` to go up.
- **Honest Accounting**: Explicitly reports **Unreadable** space (System Volume Information, shadow copies/restore points, other user profiles) and excludes online-only cloud placeholders (OneDrive/iCloud) from physical disk usage.

---

### 🧹 5. Free Up Space (`Ctrl+5`)
Reclaim disk space safely from a single dashboard. Nothing is deleted until you review, select, and confirm:
- **Recycle Bin**: Empty the Recycle Bin to reclaim space held by previously deleted files.
- **Temporary Files**: Installer and app leftovers in temp folders older than 24 hours (permanent delete).
- **Caches**: Browser caches, Electron app caches (`Cache_Data`, `Code Cache`, `GPUCache`), and GPU shader caches (`DXCache`, `GLCache`, `NV_Cache`). Offline web storage (`Service Worker`) is listed with a note and left unselected by default.
- **Developer Caches**: `npm`, `pip`, `NuGet`, `Yarn`, `pnpm`, and `Gradle` package download caches, plus `node_modules` folders untouched for over 90 days.
- **Duplicate Files**: Click **Find duplicates** to run a fast 3-pass byte-exact comparison (`Size → 64 KB Head/Tail Sample → Full XxHash128`) across user files ($\ge 1\text{ MB}$). In each duplicate group, the oldest copy is automatically unmarked so you always keep one copy.
- **Old Downloads & Large, Old Files**: Downloads older than 90 days and user files $\ge 500\text{ MB}$ untouched for a year. Never pre-selected; action moves items to the **Recycle Bin**.
- **Hibernation & System Tools**: One-click shortcuts to turn off `hiberfil.sys` (with admin prompt) or open Windows Disk Cleanup, Storage Settings, and System Restore point management.
- **Ask an AI**: Generates an anonymized prompt (replacing your username and personal file names with `<user>`, `<file>`, `<folder>`) that you can copy and paste into ChatGPT, Claude, or another AI assistant to ask whether specific caches or folders are safe to remove.

---

### 🗂️ 6. Organize (`Ctrl+6`)
Tidy up cluttered folders (such as `Downloads`) using local, deterministic rules—without reading file contents or sending data anywhere:
- **Smart (`Series`)**:
  1. Groups numbered/dated series (`dracula_idle_1.png … 37.png`, `ChatGPT Image 24 May 2025…`).
  2. Groups word families sharing the same extension and opening words (`Lecture 1 - Intro.pdf`, `Lecture 2 - Sorting.pdf`).
  3. Groups remaining items by file type.
- **Source**: Groups downloaded files by the website they came from (read from Windows' `Zone.Identifier` stream).
- **Type**: Groups items into *Installers, Archives, Disk images, Documents, Images, Videos, Audio, Code, Fonts, Torrents, Folders, Other*.
- **Session**: Clusters files downloaded within 30 minutes of each other.
- **Leftovers (`Redundant`)**: Finds archives whose extracted folder already sits next to them (`project.zip` beside `project\`) and duplicate re-downloads (`setup (1).exe` next to `setup.exe` of the exact same size).
- **Safe Move & Undo**: Move selected items into a new or existing subfolder (never overwrites existing files) and click **Undo** to put everything back.

---

## 3. Built-in Viewer & Editor

Press `Space` on any file in **Files** or **Search**, or open a file from Explorer via **Open with Grepdesk**:

| Format | Capabilities |
| :--- | :--- |
| **Text & Source Code** | Syntax highlighting (TextMate grammars), line numbers, word wrap toggle, Find (`Ctrl+F`) and Replace (`Ctrl+H`). Press `Ctrl+E` (or click **Edit**) to edit and `Ctrl+S` to save while preserving the file's original encoding and line endings. Files opened from Explorer start in Edit mode automatically. |
| **Markdown (`.md`)** | Toggle between **Rendered** view (with clickable links and tables) and **Source** editor. |
| **Word (`.docx`)** | Paginated view with headings, lists, tables, embedded images, hyperlinks, headers/footers, page numbers, and footnotes. Click **Edit** (`Ctrl+E`) for full WYSIWYG editing (styles, fonts, sizes, bold/italic/underline/strikethrough, text & highlight colors, alignment, bullets/numbering, indentation, page breaks, undo/redo). Saving modifies only changed XML parts, preserving all original images, comments, and metadata. |
| **PDF (`.pdf`)** | Smooth continuous page rendering as you scroll, with zoom (`Ctrl+Wheel`, `+`/`-`, Fit, Fit width, Actual size `1`). |
| **Images** | Supports PNG, JPEG, GIF, BMP, WebP, plus HEIC, TIFF, RAW, and SVG via Windows codecs. Zoom (`Ctrl+Wheel`, `+`/`-`), Fit (`0`), Actual size (`1`). |
| **Video & Audio** | Hardware-accelerated playback via Windows Media Foundation. Play/Pause (`K`), Seek (`←`/`→`), Mute (`M`), volume control, and multi-track audio switching (`1`–`9`). |
| **Excel (`.xlsx`)** | Read-only extracted sheet text preview. |

---

## 4. Explorer Context Menu & Command-Line Usage

Enable or disable Explorer context menu entries in **Settings** (`Ctrl+,`). All entries are stored under `HKCU\Software\Classes` (no administrator rights needed).

### CLI Flags (`Grepdesk.UI.exe`)

You can also invoke `Grepdesk.UI.exe` directly from scripts or terminals:

```powershell
# Open Grepdesk in a specific folder, or open a file in the Viewer/Editor
Grepdesk.UI.exe "C:\Projects"
Grepdesk.UI.exe "C:\Notes\todo.md"

# Extract zip archive(s) smartly (single root folder -> here; loose files -> subfolder)
Grepdesk.UI.exe --extract-here "C:\Downloads\archive.zip"

# Extract zip archive(s) into a named subfolder next to each archive
Grepdesk.UI.exe --extract-to "C:\Downloads\archive.zip"

# Compress one or more files/folders into a .zip archive
Grepdesk.UI.exe --compress "C:\Projects\MyApp" "C:\Projects\README.md"

# Paste files currently on the OS clipboard into a destination folder
Grepdesk.UI.exe --paste "D:\Backup"

# Append elapsed job time to %LOCALAPPDATA%\Grepdesk\benchmark.log
Grepdesk.UI.exe --extract-to --benchmark "C:\Downloads\archive.zip"

# Remove all Explorer context-menu entries and file associations (used by uninstaller)
Grepdesk.UI.exe --unregister
```

---

## 5. Keyboard Shortcuts Reference

### Global & Navigation
| Shortcut | Action |
| :--- | :--- |
| `Ctrl+1` … `Ctrl+6` | Switch between **Files**, **File Name Search**, **Content Search**, **Disk Usage**, **Free Up Space**, and **Organize** |
| `Ctrl+,` | Open **Settings** |
| `Ctrl+F` | Focus the search box (or the folder filter box on the **Files** page) |
| `F5` | Refresh current folder or rescan index |
| `Esc` | Return focus to the search/filter box (or clear it if already focused) |

### Files Page (Folder Browser)
| Shortcut | Action |
| :--- | :--- |
| `Enter` | Open selected folder or launch selected file |
| `Space` | Open selected file in Grepdesk Viewer |
| `Backspace` / `Alt+←` | Navigate back |
| `Alt+→` | Navigate forward |
| `Alt+↑` | Go up one folder level |
| `Ctrl+L` | Focus address bar to type a path |
| `Ctrl+H` | Show / hide hidden and system files |
| `Ctrl+Shift+S` | Calculate sizes for all subfolders in the current folder |
| `F2` | Rename selected item |
| `Ctrl+Shift+N` | Create new folder |
| `Ctrl+Z` | Undo the last rename or new folder (a new folder is removed only if still empty) |
| `Alt+Enter` | Properties of the selected item or the open folder (Windows) |

In the folder list, drag the left edge of the **Date modified**, **Type** or **Size** header to change that column's width (double-click the edge to reset it). The widths are remembered.

**View modes**: the **View** button in the toolbar switches the folder list between **Details**, **Medium icons** and **Large icons** (`Ctrl+Shift+6` / `3` / `2`). Unlike Explorer, the choice is global: it applies to every folder and is remembered, not stored per folder. The icon views show thumbnails of pictures, videos and documents where Windows has them, and the View menu also holds the sort options since the column headers are hidden there. Folders with more than 1,500 items are shown as Details.

Opening a **shortcut (`.lnk`) to a folder** shows that folder in the browser; other shortcuts open as before.

In the file viewer, **Alt+←** / **Alt+→** (or the ‹ › buttons) open the previous / next file in the same folder. For images and text the plain arrow keys do the same; for slides, PDF pages and media they keep turning pages or seeking.
| `Ctrl+X` / `Ctrl+C` / `Ctrl+V` | Cut / Copy / Paste files (interoperable with Windows Explorer) |
| `Ctrl+Shift+C` | Copy selected path(s) as text |
| `Delete` | Move selected item(s) to the Recycle Bin |

### Search Results
| Shortcut | Action |
| :--- | :--- |
| `↓` / `↑` | Move between the search box and the results list |
| `Enter` | Open selected result with default application |
| `Space` | Open selected result in Grepdesk Viewer |
| `Ctrl+Enter` | Reveal and select the item in Windows Explorer |
| `Ctrl+C` | Copy selected result's path as text |
| `Ctrl+Shift+C` | Copy the file itself to the clipboard (to paste in Explorer) |
| `Delete` | Move selected result to the Recycle Bin (with confirmation) |

### Viewer & Editor (`FileViewerWindow`)
| Shortcut | Action |
| :--- | :--- |
| `Space` / `Esc` | Close the viewer window (when not editing text) |
| `Ctrl+E` | Toggle Edit mode (Text, Code, Markdown, `.docx`) |
| `Ctrl+S` | Save changes |
| `Ctrl+F` / `Ctrl+H` | Find / Replace in text and code files |
| `Ctrl+Wheel` / `+` / `-` | Zoom in / out (Images, PDF, `.docx`) |
| `0` / `1` | Fit to window / Actual size (`100%`) |
| `K` | Play / Pause video or audio |
| `←` / `→` | Seek backward / forward in media |
| `M` | Mute / Unmute audio |
| `1` … `9` | Toggle audio tracks in multi-track videos |

### Word (`.docx`) Editor Mode
| Shortcut | Action |
| :--- | :--- |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo |
| `Ctrl+B` / `Ctrl+I` / `Ctrl+U` | Bold / Italic / Underline |
| `Ctrl+[` / `Ctrl+]` | Decrease / Increase font size by 1 pt |
| `Ctrl+Space` | Clear character formatting |
| `Ctrl+L` / `Ctrl+E` / `Ctrl+R` / `Ctrl+J` | Align Left / Center / Right / Justify |
| `Tab` / `Shift+Tab` | Increase / Decrease paragraph or list indent |
| `Ctrl+Enter` | Insert page break |

---

## 6. Troubleshooting & File Locations

- **Settings File**:
  - Windows: `%APPDATA%\Grepdesk\settings.json`
  - Linux: `~/.config/Grepdesk/settings.json`
- **Error Logs & Crash Trails**:
  - Windows: `%LOCALAPPDATA%\Grepdesk\logs\` (click **Open log folder** under **Settings → About**)
  - Linux / macOS: `~/.local/share/Grepdesk/logs/`
- **Benchmark Log**:
  - `%LOCALAPPDATA%\Grepdesk\benchmark.log` (written when `--benchmark` is passed to a job)
