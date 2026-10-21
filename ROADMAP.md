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
- [x] **One version number.** No `<Version>` in the projects; `setup.iss`
  hard-codes `1.0.0`. Set it once (e.g. `Directory.Build.props`), use it in
  the installer, and show it in an About section in Settings.
- [x] **Installer placeholders.** `AppPublisherURL=https://github.com/`,
  `AppPublisher=Grepdesk`.

## First updates

- [x] **"Start with Windows" needs a reason to exist.** (Removed for now.) There is no tray or
  background mode, so starting at login just opens a window with nothing
  scanned. Remove the option, or ship it with a tray icon and a global
  hotkey that brings up search.
- [x] **Update check.** Opt-in check against GitHub Releases on startup (at
  most daily) and a Check now button in Settings → About.
- [ ] **winget package**, so updates arrive through `winget upgrade`.
- [ ] **Memory and startup of the index.** A full-disk scan holds ~2 GB for
  ~3.5M entries and is redone on every start. Persist the index to disk and
  use a more compact layout (e.g. interned parent folders, struct arrays).
- [x] **Manual test checklist** ([docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md)) for each release: keyboard shortcuts, drag
  and drop, delete / move / undo flows, Organize, Free Up Space. These have
  no automated UI tests.

## Explorer replacement

Goal: Grepdesk as the everyday file manager and file opener, with search,
disk usage, cleanup and the fast engines one click away. The folder browser
becomes the first page; search runs in the folder being browsed. Phases in
order; each one is usable on its own.

### Phase 0: groundwork
- [ ] **Resident process.** `SingleInstanceHost` also takes plain starts
  (`OpenFolder`, `OpenFile` commands): a second launch hands its path over the
  pipe and exits, the primary opens the window. Needed before Grepdesk opens
  folders for Explorer: a cold Avalonia start is too slow for that.
- [ ] **Tray icon and global hotkey** on top of the resident process.
- [ ] **Split `MainWindow.axaml.cs`** (see Code health).
- [ ] **Persisted index kept current from the NTFS USN journal**, so search
  in the browsed folder is instant after a restart.

### Phase 1: folder browser
- [x] `FolderListing`, `NavigationHistory`, `AddressResolver` in Core, with tests.
- [x] Browse page: back / forward / up / refresh, breadcrumb address bar that
  turns into a text box (Ctrl+L), places sidebar (user folders, drives with
  free space).
- [x] Details list sorted by name / date / type / size, folders first;
  filter the folder as you type (Ctrl+F); live updates from a file watcher.
- [x] Keyboard: Enter, Backspace, Alt+←/→/↑, F5, type-ahead; Space opens
  the viewer; the preview pane follows the selection.
- [x] Opening a folder with Grepdesk (Explorer menu, command line) opens it
  in the browser.
- [x] Explorer's icons (`IShellItemImageFactory` on one STA thread), cached per
  extension, per item for apps, shortcuts and folders; only for rows on screen.
- [x] Remember the last folder and the sort order.
- [ ] Resizable columns, remembered.
- [ ] `.lnk` shortcuts to folders open in the browser instead of Explorer.

### Phase 2: file operations
- [x] Rename (F2), new folder (Ctrl+Shift+N), Recycle Bin (Delete), on a
  multiple selection where it makes sense.
- [x] Copy / cut / paste (Ctrl+C / X / V) through the system clipboard, so it
  works both ways with Explorer; pastes run as the existing transfer job.
- [ ] Ctrl+Z for rename, new folder, move and trash.
- [ ] Drag and drop between folders and to / from Explorer.
- [ ] **The system context menu** (`IContextMenu` hosting) so third-party
  entries (7-Zip, Git, editors) show up. The hardest item in this phase.
- [ ] Properties dialog (`SHObjectProperties`), "Open with" submenu.

### Phase 3: better than Explorer
- [ ] Tabs and a dual pane; restore them on start.
- [ ] Search below the current folder from the index; folder size column.
- [ ] Grid / thumbnail view, grouping.
- [ ] Viewer: previous / next file in the folder with ←/→.
- [ ] Browse into `.zip` (and later `.7z`, `.rar`, `.tar`) like a folder.

### Phase 4: default file manager and opener
- [ ] **"Use as default file manager"** setting: HKCU `open` verbs for
  `Directory`, `Drive`, `Folder`, and Win+E. Virtual locations (`shell:`,
  `::{CLSID}`, Control Panel, network, phones) are passed on to
  `explorer.exe`. Turned off by the setting, `--unregister` and the
  uninstaller; a start-up check restores Explorer if Grepdesk's entry points
  at a missing exe.
- [ ] Register under `RegisteredApplications` with `Capabilities`, so Grepdesk
  shows in Settings → Default apps with all its types; a button that opens
  that page (Windows only lets the user pick a default app).

### Phase 5: polish and trust
- [ ] Light theme / follow the system theme, Mica.
- [ ] Cloud file states (OneDrive placeholders) in the list.
- [ ] Code signing (see Before release): an unsigned default handler is
  worse than an unsigned tool.

## Features

- [ ] **Copy speed to another drive.** Copying 10,000 small files from C: to
  D: takes ~10 s whatever the worker count, while robocopy takes ~1.4 s.
  Found so far: not the engine (plain parallel `File.Copy` behaves the same);
  creating empty files on D: is fast, writing content is ~1 ms per file and
  doesn't scale with threads (Defender's scan on close fits); 8 workers beat
  32 on both drives in some runs, so `WorkerPolicy`'s 32 may be too many.
  Runs were noisy: measure with more repetitions in random order, and look at
  the filter stack (`fltmc instances -v D:`, admin) and a WPR trace of
  robocopy vs Grepdesk.
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
- [ ] **Tray icon and global hotkey**: keep the index in the background and
  bring up search from anywhere. With that, "Start with Windows" makes sense
  again (an installer task writing the `Run` key).
- [ ] **Light theme / follow the system theme.**

## Code health

- [ ] **Split `MainWindow.axaml.cs`** (~1,200 lines): move file name and
  content search into their own views, like Disk Usage, Free Up Space and
  Organize.
- [ ] **Linux and macOS** compile but were never run; Trash handling and the
  disk classifier's location rules are written for Windows first. Test them,
  or say "tested on Windows" in the README until then.
