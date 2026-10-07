# Roadmap

What is left before and after a first public release. Roughly in priority
order within each section.

## Before release

- [x] **Clean up Explorer menu entries on uninstall.** `setup.iss` doesn't
  remove the shell integration keys (`HKCU\Software\Classes\...\Grepdesk*`),
  so after uninstalling, "Open with Grepdesk" / "Paste with Grepdesk" stay in
  the right-click menu pointing at a deleted exe. Add `uninsdeletekey`
  registry entries (or run the app's own `Disable` for every feature).
- [x] **Catch and log unexpected errors.** There is no global handler
  (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`,
  Avalonia's dispatcher), and several event handlers are `async void`, so a
  failure closes the app without a trace. Write a log under
  `%LOCALAPPDATA%\Grepdesk\logs` and show "something went wrong, the log is
  here" instead of disappearing. Matters most around delete and move.
- [x] **Build with stable versions.** The build uses a .NET 10 RC SDK, and PDF
  reading uses a PdfPig alpha (`0.1.16-alpha-…`). Move both to stable releases.
- [x] **Self-contained publish (or a runtime check).** The installer copies
  the `publish` folder as is; on a machine without .NET 10 the app won't
  start. Publish self-contained, or have the installer check for the runtime.
- [ ] **Code signing.** Unsigned, the exe gets SmartScreen's "unrecognized app"
  warning, and a tool that deletes files and runs from the Explorer menu is
  prone to antivirus heuristics. Needs a certificate.
- [ ] **One version number.** No `<Version>` in the projects; `setup.iss`
  hard-codes `1.0.0`. Set it once (e.g. `Directory.Build.props`), use it in
  the installer, and show it in an About section in Settings.
- [ ] **Installer placeholders.** `AppPublisherURL=https://github.com/`,
  `AppPublisher=Grepdesk`.

## First updates

- [ ] **"Start with Windows" needs a reason to exist.** There is no tray or
  background mode, so starting at login just opens a window with nothing
  scanned. Remove the option, or ship it with a tray icon and a global
  hotkey that brings up search.
- [ ] **Update check.** No way to get a fix to users. At least an opt-in
  check against GitHub Releases on startup; better, a winget package.
- [ ] **Memory and startup of the index.** A full-disk scan holds ~2 GB for
  ~3.5M entries and is redone on every start. Persist the index to disk and
  use a more compact layout (e.g. interned parent folders, struct arrays).
- [ ] **Manual test checklist** for each release: keyboard shortcuts, drag
  and drop, delete / move / undo flows, Organize, Free Up Space. These have
  no automated UI tests.

## Features

- [ ] **Copy & Verify** for the copy/move engine: hash source and destination
  (XXH3 default, SHA-256 optional). Read the destination unbuffered, or the
  check only sees the OS cache. For moves, delete the source only after the
  destination verified.
- [ ] **Archive preview and extraction beyond zip**: list `.rar`, `.7z`,
  `.tar` contents in the preview panel (SharpCompress, MIT; reading only,
  RAR can't be created), then extract them with the existing job window.
- [ ] **Excluded folders** in Settings (`node_modules`, `.git`, `bin`, `obj`
  by default) for name search.
- [ ] **Bulk actions in search results** (copy paths, zip, trash a
  selection). Organize already offers the safer, group-level form.
- [ ] **Light theme / follow the system theme.**

## Code health

- [ ] **Split `MainWindow.axaml.cs`** (~1,200 lines): move file name and
  content search into their own views, like Disk Usage, Free Up Space and
  Organize.
- [ ] **Linux and macOS** compile but were never run; Trash handling and the
  disk classifier's location rules are written for Windows first. Test them,
  or say "tested on Windows" in the README until then.
