# Grepdesk CLI (`grepdesk`) Specification

This document defines the architecture, design, and command specification for `grepdesk` (or alias `gd`), a high-performance command-line interface built on top of [`Grepdesk.Core`](../Grepdesk.Core).

---

## 1. Vision & Goals

Grepdesk CLI brings the speed and capabilities of Grepdesk's desktop engines directly to the terminal for two primary audiences:

1. **Human Developers & Power Users**: A fast, unified alternative to Windows PowerShell cmdlets and Unix tools (`fd`, `ripgrep`, `7z`, `robocopy`) that works out of the box with zero external runtime dependencies.
2. **AI Coding Agents & Automation Scripts**: Provides token-efficient, machine-readable structured output (`--json` / `--jsonl`), precise match snippets, and the unique ability to search binary documents (`.pdf`, `.docx`) that standard developer tools cannot inspect.

---

## 2. Architecture

```
┌──────────────────────────────────────────────┐
│                 Grepdesk.sln                 │
├───────────────────────┬──────────────────────┤
│      Grepdesk.UI      │     Grepdesk.Cli     │
│  (Desktop / Avalonia) │   (Console Binary)   │
└───────────┬───────────┴──────────┬───────────┘
            │                      │
            ▼                      ▼
┌──────────────────────────────────────────────┐
│                Grepdesk.Core                 │
│  - FileIndex          - ContentSearcher      │
│  - ZipCompressor      - ZipExtractor         │
│  - TransferEngine     - DuplicateFinder      │
│  - CleanupAnalyzer    - Document Extractors  │
└──────────────────────────────────────────────┘
```

- **Project Type**: `<OutputType>Exe</OutputType>` targeting `net10.0`.
- **Subsystem**: Unlike `Grepdesk.UI.exe` (`WinExe`), `grepdesk.exe` runs in the console subsystem, attaching directly to standard input/output streams (`stdout`, `stderr`, `stdin`).
- **Packaging**: Both `Grepdesk.UI.exe` and `grepdesk.exe` are packaged side-by-side in the Inno Setup installer and the portable `.zip`. The installer optionally adds the install directory to the user's `PATH`.

---

## 3. Subcommand Specifications

### 3.1 `grepdesk find` (File Name Search)
Instant file and directory name discovery using `FileIndex`.

```bash
grepdesk find <pattern> [options]
```

#### Options:
- `-p, --path <dir>`: Root path to search (default: current directory `.`).
- `-t, --type <file|dir|archive|code|image|doc>`: Filter by file type classification.
- `-e, --ext <ext1,ext2>`: Filter by specific extension list (e.g. `cs,xaml`).
- `--hidden`: Include hidden and system files.
- `--max-depth <n>`: Limit directory recursion depth.
- `--json`: Output as a JSON array of matching file metadata.

---

### 3.2 `grepdesk grep` (Deep Content Search)
Search file contents across source code, text files, Markdown, Office documents (`.docx`), and PDFs (`.pdf`).

```bash
grepdesk grep <query> [options]
```

#### Options:
- `-p, --path <dir>`: Search root (default: current directory `.`).
- `-e, --ext <ext1,ext2>`: Restrict content search to extensions (e.g. `pdf,docx,cs,md`).
- `-i, --ignore-case`: Case-insensitive search (default: true).
- `-s, --case-sensitive`: Force case-sensitive search.
- `-c, --context <n>`: Context characters or lines around the match (default: 60).
- `--json`: Output structured JSON/JSONL with match offsets, lines, and snippets.
- `--max-matches <n>`: Stop after finding $N$ matches.

---

### 3.3 `grepdesk zip` & `grepdesk unzip` (High-Speed Archiving)
Parallel multi-core zip engine powered by `ZipCompressor` and `ZipExtractor`.

```bash
# Compress files or folders into an archive
grepdesk zip <output.zip> <inputs...> [-l <1-9>]

# Extract an archive
grepdesk unzip <archive.zip> [-o <destination>] [--benchmark]
```

#### Features:
- Automatically detects already-compressed media (JPEG, MP4) and stores them uncompressed to maximize speed.
- Appends files concurrently from multiple worker threads directly to the output stream.

---

### 3.4 `grepdesk duplicates` (Duplicate File Finder)
Identifies duplicate files by fast size grouping followed by chunked content hashing (`DuplicateFinder`).

```bash
grepdesk duplicates <path> [--min-size <size>] [--delete-prompt] [--json]
```

#### Options:
- `--min-size <size>`: Minimum file size to consider (e.g. `10MB`, `1GB`).
- `--json`: Machine-readable grouped list of duplicate file paths.

---

### 3.5 `grepdesk du` (Disk Usage Summary)
Summarizes folder space and highlights the largest consumers.

```bash
grepdesk du <path> [--top <n>] [--json]
```

---

## 4. Agent Integration & Structured Output

To make Grepdesk a first-class utility for AI agents (Antigravity, Claude Code, Cursor, Copilot), the CLI supports streaming JSON Lines (`--jsonl`) or a single JSON response (`--json`).

### Example: Content Search JSON Output
```bash
grepdesk grep "API_SECRET" --path ./docs --ext pdf,docx,md --json
```

```json
{
  "query": "API_SECRET",
  "total_matches": 1,
  "elapsed_ms": 142,
  "results": [
    {
      "file": "docs/architecture_spec.pdf",
      "format": "pdf",
      "page": 7,
      "line": 34,
      "match": "API_SECRET",
      "snippet": "...the client authenticates by passing API_SECRET inside the authorization header..."
    }
  ]
}
```

### Exit Codes:
- `0`: Matches found / command executed successfully.
- `1`: No matches found (for `find` and `grep`).
- `2`: Invalid arguments or runtime execution failure.

---

## 5. Implementation Phases

- [ ] **Phase 1: Project Setup & CLI Shell**
  - Create `Grepdesk.Cli` project in `Grepdesk.sln`.
  - Implement command routing and argument parsing.
- [ ] **Phase 2: Search Subcommands (`find` & `grep`)**
  - Expose `FileIndex` and `ContentSearcher` with plain text & `--json` formatters.
  - Enable PDF (`PdfPig`) and Word (`.docx`) extractors in headless mode.
- [ ] **Phase 3: Archive Subcommands (`zip` & `unzip`)**
  - Expose `ZipCompressor` and `ZipExtractor` with terminal progress bar.
- [ ] **Phase 4: Maintenance Subcommands (`duplicates` & `du`)**
  - Implement duplicate and disk space reporting.
- [ ] **Phase 5: Packaging & Installer Integration**
  - Include `grepdesk.exe` in `setup.iss` and portable zip scripts.
  - Add optional "Add to PATH" task in installer.
