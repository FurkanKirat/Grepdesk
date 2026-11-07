# Grepdesk Documentation

Welcome to the **Grepdesk** documentation. Grepdesk is a fast, native desktop file manager and utility suite for Windows (with cross-platform core support for Linux and macOS) that combines file browsing, instant file name & content search, disk usage analysis, space cleanup, smart folder organization, document viewing/editing, and multi-core zip/copy/move engines.

---

## 📚 Documentation Index

| Document | Audience | Description |
| :--- | :--- | :--- |
| **[User Guide](USER_GUIDE.md)** | End Users | Complete guide to all features, pages, the built-in viewer/editor, Explorer context menu integration, CLI flags, keyboard shortcuts, and troubleshooting. |
| **[Architecture & Internals](ARCHITECTURE.md)** | Contributors & Developers | Deep dive into solution structure, core engines (`FileIndex`, `ZipCompressor`, `TransferEngine`, `DuplicateFinder`, `DocxEditDocument`), IPC batching, platform abstractions, and diagnostics. |
| **[Build & Packaging Guide](BUILD.md)** | Contributors & Maintainers | Prerequisites, local development, running unit and performance tests, `build.py` / `build.ps1` automation, self-contained publishing, and Inno Setup installer compilation. |
| **[CLI Specification](CLI.md)** | Developers & AI Agents | Design and command specification for the headless `grepdesk` command-line interface (`find`, `grep`, `zip`, `unzip`, `duplicates`, `du`). |
| **[Roadmap](ROADMAP.md)** | Everyone | Project milestones, Explorer-replacement phases, planned features, and code health goals. |
| **[Release Checklist](RELEASE_CHECKLIST.md)** | Maintainers | Step-by-step manual verification checklist for builds, installers, shell integration, and data-sensitive file operations prior to publishing a release. |
| **[AGENTS.md](../AGENTS.md)** | AI Coding Agents | Repository rules, build/test commands, architectural boundaries, and mandatory invariants for AI assistants. |

---

## 🗂️ Repository Layout

```text
Grepdesk/
├── Grepdesk.Core/          # Cross-platform engines, models, and platform interfaces
├── Grepdesk.UI/            # Avalonia UI desktop application, views, viewer/editor, and job windows
├── Grepdesk.Windows/       # Windows implementations (Win32, COM, CopyFile2, Registry, Media Foundation)
├── Grepdesk.Linux/         # Linux implementations (freedesktop.org, xclip/wl-clipboard, sysfs)
├── Grepdesk.MacOS/         # macOS implementations (Finder/open, diskutil)
├── Grepdesk.Tests/         # xUnit test suite (unit, integration, and performance guard tests)
├── Grepdesk.Benchmarks/    # Reproducible benchmark runner vs. Explorer, 7-Zip, and robocopy
├── docs/                   # Project documentation
├── Directory.Build.props   # Centralized version and product metadata
├── build.py / build.ps1    # Automated cross-platform build and packaging scripts
└── setup.iss               # Inno Setup script for the Windows installer
```
