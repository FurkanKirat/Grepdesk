# Release checklist

Run this before every release, on Windows, with the **installed** build (not
`dotnet run`). The UI has no automated tests, so this is what catches a
broken shortcut or a delete that goes wrong. Takes about 30–40 minutes.

Use throwaway data: make a folder like `C:\gd-test` with a few hundred files
(some text, images, a PDF, a .docx, a video, a few zips, a `node_modules` copy)
and work only inside it for anything that deletes or moves.

## 1. Build

- [ ] `Directory.Build.props` has the new version; `ROADMAP.md` is current
- [ ] `dotnet test` passes
- [ ] `dotnet publish Grepdesk.UI -p:PublishProfile=win-x64`
- [ ] Compile `setup.iss` (Inno Setup) → `Output\Grepdesk-vX.Y.Z-win-x64-Setup.exe`
- [ ] Portable zip: `publish\win-x64` zipped as `Grepdesk-vX.Y.Z-win-x64-portable.zip`

## 2. Install, upgrade, uninstall

- [ ] Upgrade over the previous release: installs without asking to uninstall
      first; Settings → About shows the new version
- [ ] Settings → tick all four Explorer menu features; each appears in the
      right-click menu (Windows 11: **Show more options**)
- [ ] Uninstall: Start menu entry, desktop shortcut and program folder are gone,
      **and the Explorer menu entries are gone too**
- [ ] Fresh install on a machine without .NET (a VM, or another PC): the app starts

## 3. Delete, move and undo (data loss risk; do these first)

- [ ] Search result → `Delete` → confirmation appears; Cancel keeps the file,
      confirm moves it to the **Recycle Bin** (restorable)
- [ ] Organize → move a group into a subfolder → files moved, nothing
      overwritten when a name is taken (the item is skipped with a reason) →
      **Undo** puts everything back
- [ ] Organize → move a group to the Recycle Bin after confirmation
- [ ] Free Up Space → select a few items → confirm → only the selected ones go;
      Recycle Bin items are restorable, caches are gone
- [ ] Free Up Space → Duplicates: the oldest copy is the one kept
- [ ] Paste with Grepdesk (cut + paste) across drives: source deleted only after
      the copy finished; cancel halfway leaves the source intact
- [ ] Paste onto existing files: conflict dialog only when data would be lost;
      Skip / Overwrite / Keep both do what they say

## 4. Jobs from Explorer

- [ ] Compress a folder and several files at once (one job window, not many)
- [ ] Extract here / Extract to folder on a zip, including one with non-ASCII names
- [ ] Copy a large folder: progress, speed and time left move; Pause and Resume
      work; Cancel stops it
- [ ] Open with Grepdesk on a folder and on a folder background: search opens there

## 5. Search

- [ ] Choose a folder, and Scan whole PC: results appear while typing, matches highlighted
- [ ] Empty box lists everything; sorting by name / size / date works on all results
- [ ] Type, size and date filters
- [ ] Drop a folder on the window: it is scanned
- [ ] Content search finds text in .txt, .cs, .pdf, .docx; snippets shown
- [ ] Drag a result into Explorer: copied, the original stays

## 6. Keyboard (Settings shows the full table)

- [ ] `Ctrl+F` focuses search from any page; `Ctrl+1` … `Ctrl+5` switch pages; `Ctrl+,` opens Settings
- [ ] `↓` from the search box into results, `↑` at the top goes back; `Esc` returns to the box
- [ ] `Enter` opens, `Ctrl+Enter` shows in Explorer
- [ ] `Ctrl+C` copies the path, `Ctrl+Shift+C` the file (paste it in Explorer)
- [ ] `Space` on a text / code / Markdown file opens the viewer; `Space` or `Esc` closes it;
      typing a space in the viewer's `Ctrl+F` box doesn't close it
- [ ] `F5` rescans

## 7. Preview and viewer

- [ ] Preview panel: image, PDF, .docx, .md (rendered, links clickable), code file,
      folder, video (frame + duration), .pptx (first slide), .mp3
- [ ] Viewer: a large log file scrolls smoothly; a minified .js shows the
      "very long lines" message; Markdown Rendered / Source toggle
- [ ] Opened from content search, the viewer selects the first match

## 8. Disk Usage

- [ ] Scan a drive: categories add up, games and apps listed as whole items
- [ ] Drill into folders from the root; unreadable space reported separately

## 9. Settings and the rest

- [ ] Switch language English ↔ Türkçe: every page, menus and the Explorer
      entries' labels follow
- [ ] Hide / show the preview panel; it hides on a narrow window
- [ ] About: version and commit shown; Open log folder and GitHub open
- [ ] Check now: "latest version" (or the new release, if one is out)
- [ ] Settings survive a restart

## 10. Publish

- [ ] Tag `vX.Y.Z`, create the GitHub release with the Setup exe and portable zip
- [ ] Release notes: what changed, anything users have to do
- [ ] On a machine with the previous version and the update check on: the
      sidebar notice shows the new version and opens the release page
