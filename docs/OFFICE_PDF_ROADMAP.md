# Office & PDF Roadmap

Ordered implementation plan for closing the gaps across Grepdesk's built-in **PDF**, **Word** (`.docx`), **Excel** (`.xlsx`), and **PowerPoint** (`.pptx` / `.ppt`) viewers, editors, preview pane, content search, and standalone CLI (`grepdesk`).

Phases are ordered from highest user impact and architectural leverage (cross-cutting viewer workflows and core reading fixes) to heavier specialized capabilities (annotations, form filling, and OCR).

---

## Baseline (Already Implemented)

- **PDF**: Continuous-scroll PDFium rendering via `Docnet.Core` (`PdfRenderer`, `PdfView`), zoom & fit-width, lazy page bitmap caching with pixel budget (`KeptPixelsBudget`), character-level drag/word/page text selection and copy (`PdfPageText`, `PdfSelectionLayer`), content search & CLI text extraction via `PdfPig` (`PdfExtractor`, `PdfPreviewText`, `OpenCommand`, `GrepCommand`).
- **Word (`.docx`)**: Page-based layout viewer and lossless XML in-place editor (`DocxDocument`, `DocxEditDocument`, `DocxView`, `DocxEditor`, `DocxToolbar`) supporting styles, fonts, colors, highlights, alignment, lists, tables, images, headers/footers, notes, and undo/redo.
- **Excel (`.xlsx`)**: Tabbed grid viewer and lossless in-place editor (`XlsxDocument`, `XlsxEditDocument`, `XlsxView`) supporting cell edits, basic formatting, merged cells, formula bar, undo/redo, and basic formula evaluation (`+`, `-`, `*`, `/`, `SUM`, `AVERAGE`, `MIN`, `MAX`, `COUNT`).
- **PowerPoint (`.pptx` / `.ppt`)**: Slide viewer with lazy thumbnail sidebar, master/layout backdrop, text shapes, tables, images, speaker notes, and fullscreen slide show (`PptxDocument`, `PptReader`, `PptxView`, `PptxSlideShowWindow`), plus lossless `.pptx` shape/notes editing (`PptxEditDocument`).

---

## Phase 1 — Unified In-Viewer Search (`Ctrl+F` / `F3`) & Search Deep-Linking

Why first: `FileViewerWindow` already provides a shared Find bar for code/text and rendered Markdown, yet PDF and all three Office viewers currently hide the Find button (`FindButton.IsVisible = false`) and ignore `Ctrl+F`. Opening a PDF search hit also fails to jump to or highlight the matching page.

- [x] **Shared searchable viewer contract** (`ViewerBar.cs`, `FileViewerWindow.axaml`, `FileViewerWindow.axaml.cs`):
  - Add `ISearchableViewerBody` (`ViewerSearchOptions`, `ViewerSearchResult`) and wire the existing `EditorFindBar` (`MatchCase`, `WholeWord`, `Regex`, match counter, `F3` / `Shift+F3` navigation, `Esc` dismiss) across PDF, Word, Excel, and PowerPoint.
  - Refresh live match highlights when edited documents change in Word, Excel, or PowerPoint, and preserve the Find bar across Read ↔ Edit mode transitions and file reload.
  - Guard text-editor-only shortcuts (`Ctrl+H`, `Ctrl+G`, `Ctrl+Shift+O`, `Shift+Alt+F`, `Ctrl+/`) so they do not fire when an Office or PDF body is active.
- [x] **Core cross-format search engine** (`Grepdesk.Core/Viewer/DocumentSearch.cs`):
  - PDF search (`SearchPdfPage`, `SearchPdf`) normalizing line breaks and PDFium soft hyphens (`\u0002`) back to exact `PdfPageText` glyph offsets and bounding boxes.
  - Word / paragraph block search (`SearchBlocks`) shared by both read-only `DocxView` and `DocxEditor`.
  - Excel search (`SearchXlsx`) matching both formatted display values and formulas (`=…`) across all sheets in row/column order.
  - PowerPoint search (`SearchPptx`) across text shapes, table cells, and speaker notes (supporting cross-run spans).
- [x] **View highlighting & scroll-to-match**:
  - `PdfView` & `PdfSelectionLayer`: pass content-search `_highlight` into `PdfView`, auto-jump and highlight matches on open, draw passive (`#ffd400`) and active (`#ff8c00`) match boxes, and retain highlights across lazy page disposal/reload.
  - `DocxView` & `DocxEditor`: highlight cross-run spans in read-only `DocxView`, draw search rects inside `EditableParagraph.Render` in edit mode, and scroll smoothly to the active match without stealing focus from `FindBox`.
  - `XlsxView`: highlight matching cells, switch sheets automatically when stepping across matches, scroll the viewport to the matched cell, and handle matches outside the 500×60 grid cap gracefully.
  - `PptxView`: highlight matches across runs in shapes, table cells, and speaker notes (revealing hidden notes when a note matches), while keeping sidebar thumbnails and fullscreen slide show clean.
- [x] **macOS `Cmd` shortcut parity** (`PdfView.cs`, `XlsxView.cs`, `PptxView.cs`):
  - Respect `KeyModifiers.Meta` on macOS alongside `KeyModifiers.Control` on Windows/Linux (matching `DocxEditor.cs`).

---

## Phase 2 — PDF Essential Viewer & Preview Upgrades

Why second: PDF is the most common read-only document type; navigation, orientation, and preview fidelity gaps affect daily browsing and searching.

- [ ] **Navigation & page chrome** (`PdfView.cs`):
  - Interactive **Go to page** box (`Ctrl+G` / clickable page counter) and **Previous / Next page** toolbar buttons.
  - Toggleable **sidebar** with lazy page thumbnails and clickable **document outline / bookmarks** tree.
  - Clickable **PDF hyperlinks** (external URLs via `OpenRequested` and internal page/destination links).
- [ ] **View modes & reading controls** (`PdfView.cs`, `PdfRenderer.cs`):
  - **Page rotation** (90° CW / CCW) in the viewer.
  - **Fit-to-Page** and **100% actual size** buttons alongside Fit-Width.
  - **Single-page** and **two-page facing** layout modes, **hand/pan drag** when zoomed, and **dark / inverted reading mode**.
  - Virtualize page host containers so PDFs over 3,000 pages (`PdfRenderer.MaxPages`) open without eagerly allocating thousands of controls.
- [ ] **Preview, extraction & encrypted files** (`PdfPreviewProvider`, `PdfExtractor.cs`, `OpenCommand.cs`, `GrepCommand.cs`):
  - Render a visual page-1 thumbnail in `PdfPreviewProvider` via `PdfRenderer` so scanned/image-only PDFs preview visually instead of showing "no text layer".
  - Switch `PdfExtractor.cs`, `GrepCommand.cs`, and `OpenCommand.cs` from raw `page.Text` to layout-aware `ContentOrderTextExtractor.GetText(page)` (matching `PdfPreviewText.cs`) so adjacent words aren't fused together.
  - Prompt for a password when opening password-protected PDFs (passing password to `DocLib` / `PdfPig`).

---

## Phase 3 — Excel Usability, Scaling & Formula Engine

Why third: `XlsxView` has solid foundations (`XlsxDocument` / `XlsxEditDocument`), but real-world workbooks hit its 500×60 cap, lack sticky headers and range editing, and fail on common Excel formulas.

- [ ] **Virtualized grid & viewport ergonomics** (`XlsxView.cs`):
  - Replace the eager 500×60 control grid with a virtualized cell canvas/viewport so large sheets render without truncation or UI stalls.
  - Sticky column (`A, B, C`) and row (`1, 2, 3`) headers that stay pinned while scrolling, plus parsing/honoring frozen panes (`sheetViews` / `pane`).
  - Auto-scroll the viewport when moving the active cell with arrow keys or `Tab` / `Enter`.
  - Honor explicit row heights (`<row ht="...">`), hidden rows/columns (`hidden="1"`), wrapped multi-line cell heights, and interactive column/row border drag-resizing.
  - Expose hidden/veryHidden sheets via a toggle or context menu when visible sheets also exist (`XlsxDocument.cs`).
- [ ] **Selection & editing workflow** (`XlsxView.cs`):
  - Multi-cell range selection (mouse drag, `Shift+Arrow`, `Ctrl+A`, clicking row/column headers).
  - Range `Ctrl+C` (TSV), `Ctrl+X` cut, `Ctrl+V` paste (including multi-cell TSV from Excel/clipboard), and `Delete` over ranges.
  - Direct **type-to-edit** on a selected cell (without needing `F2` or double-clicking the formula bar first).
  - Status bar live aggregates (`Sum`, `Average`, `Count`) for the active selection.
- [ ] **Formula engine & recalculation** (`XlsxDocument.cs`):
  - Replace the ad-hoc `TryEvalExpr` parser with a proper tokenizer/recursive-descent evaluator supporting parentheses grouping (`(A1+B1)*2`), unary `+`/`-`, `^`, comparison operators, string literals, cross-sheet references (`Sheet2!A1`), named ranges, shared formulas (`t="shared"`), and core functions (`IF`, `AND`, `OR`, `NOT`, `SUMIF`, `COUNTIF`, `COUNTA`, `ROUND`, `ABS`, `VLOOKUP`, `XLOOKUP`, `INDEX`, `MATCH`, `CONCAT`).
  - Automatically recalculate dependent formula cells in `XlsxEditDocument.SetCell` when referenced cells change (updating cached `<v>` and `DisplayText`).
- [ ] **Formatting, styles & sheet operations** (`XlsxDocument.cs`, `XlsxView.cs`):
  - Preserve cell `numFmtId` when rendering/re-evaluating formulas without cached `<v>`; support currency, accounting, scientific notation, custom date patterns, and negative-number colors.
  - Deduplicate `<font>`, `<fill>`, `<border>`, and `<xf>` entries in `XlsxEditDocument.DeriveCellStyle` instead of appending on every format click.
  - Parse and render cell borders; add font family/size, text/fill color, wrap text, vertical alignment, border, and number-format controls to `XlsxView`.
  - Add/delete/rename/reorder sheets, insert/delete rows and columns, merge/unmerge cells, sort & AutoFilter.
  - Render embedded charts/images/comments/hyperlinks/conditional formatting, and offer a tabular grid toggle for `.csv` / `.tsv` files.

---

## Phase 4 — Missing Format Coverage, Preview & CLI Polish

Why fourth: Significant format gaps and small search/CLI inconsistencies can be closed cleanly by extending `ExtractorRegistry`, `ViewerFormats`, `CompoundFile`, and `PreviewProviders`.

- [ ] **Quick-win OOXML extensions** (`ViewerFormats.cs`, `DocxExtractor.cs`, `XlsxExtractor.cs`, `PptxExtractor.cs`):
  - Support `.docm`, `.dotx`, `.dotm`, `.xlsm`, `.xltx`, `.xltm`, `.pptm`, `.ppsx`, `.ppsm`, `.potx` (same ZIP+XML schemas as `.docx`/`.xlsx`/`.pptx`).
- [ ] **OpenDocument formats** (`.odt`, `.ods`, `.odp`):
  - Add ZIP+XML content extractors and viewer/search/CLI integration (`OdtDocument` / `OdsDocument` / `OdpDocument` mapped to existing Word/Excel/PowerPoint views or read-only layouts).
- [ ] **Legacy & interchange Office formats** (`.doc`, `.xls`, `.xlsb`, `.rtf`, `.pps`):
  - Build `.doc` (MS-DOC) and `.xls` (BIFF8) readers on top of the existing `CompoundFile` OLE2 parser, plus `.rtf` text/rich-text parsing.
- [ ] **Other document formats**:
  - `.epub`, `.xps` / `.oxps`, `.eml` / `.msg` viewers and search extractors.
- [ ] **Preview Pane & Shell thumbnail fixes** (`PreviewProviders.cs`, `IPreviewProvider.cs`):
  - Fix the unreachable `ShellPreviewProvider` entries for `.pptx`/`.ppt` (currently shadowed by `DocumentPreviewProvider` appearing earlier in `PreviewProviders.Providers`).
  - Render visual first-page/first-slide previews for Office files in the Preview Pane instead of text-only summaries.
- [ ] **Content Search, Metadata & CLI completeness** (`DocxExtractor.cs`, `XlsxExtractor.cs`, `PptxExtractor.cs`, `GrepCommand.cs`, `OpenCommand.cs`):
  - Search `word/comments.xml`, Excel `xl/comments*.xml` & charts, and PowerPoint SmartArt (`ppt/diagrams/data*.xml`), charts, and slide comments.
  - Read and display document properties/metadata (`docProps/core.xml`, PDF Info dictionary: Author, Title, Subject, Created/Modified) in the viewer and `grepdesk open --json`.
  - Report slide numbers for `.pptx`/`.ppt` and sheet/cell refs for `.xlsx` in `GrepCommand`, always emit `total_pages` / sheet entries for `.xlsx` in `OpenCommand --json`, and make the 20 MB search size cap configurable or warn clearly.
  - Update `GrepCommand` and `OpenCommand` `--help` descriptions to reflect all supported document formats.

---

## Phase 5 — Word & PowerPoint Editing & Fidelity

Why fifth: Both editors work cleanly for everyday text edits on standard files; the next step is lifting structural limitations and improving layout fidelity without compromising lossless XML preservation.

- [ ] **Word (`.docx`) editor capabilities** (`DocxEditDocument.cs`, `DocxEditor.cs`, `DocxToolbar.cs`):
  - Table editing: insert table, insert/delete rows and columns, merge/split cells, and `Tab` / `Shift+Tab` navigation between cells.
  - Insert, delete, and resize inline images; insert and edit hyperlinks (preserve link wrappers across copy/paste and allow `Ctrl+Click` to follow links in edit mode).
  - Superscript/subscript toolbar toggles (`DocxRunChange.Baseline`), line spacing and paragraph spacing controls, custom font size input, and expanded/custom color picker.
  - Editable headers, footers, footnotes, and text boxes (`Source` support for text boxes and active header/footer regions).
  - Page setup dialog (margins, orientation, paper size, section breaks), live word/character count, spellcheck integration, and rich HTML/RTF clipboard interoperability with external apps.
- [ ] **Word (`.docx`) rendering fidelity** (`DocxDocument.cs`, `DocxView.cs`):
  - Parse `w:spacing w:line` (line spacing), paragraph borders (`w:pBdr`), per-cell/styled table borders, `smallCaps`, tab stops with leader dots, even/odd headers/footers, and multi-column sections (`w:cols`).
  - Display tracked changes (`w:ins`, `w:del`) and comments (`word/comments.xml`) with optional show/hide markup toggle.
  - Render floating image positioning (`wp:anchor`), positioned text boxes, shapes, EMF/WMF/SVG/TIFF images, and formatted math (`oMath`).
  - Heading navigation outline sidebar, internal bookmark links (`HYPERLINK \l`), per-page footnote placement, cross-paragraph selection in read-only mode, RTL/BiDi layout, and approximate page break reflow during editing.
- [ ] **PowerPoint (`.pptx` / `.ppt`) rendering & editing** (`PptxDocument.cs`, `PptxView.cs`, `PptxSlideShowWindow.cs`):
  - Apply `grpSp` group child coordinate transforms (`chOff`/`chExt`) in `PptxReader` (fixing misplaced grouped shapes in `.pptx`).
  - Replace lossy `RewriteTxBodyText` in `PptxEditDocument` with run/paragraph-preserving edits and selection-level formatting (bold, italic, underline, strikethrough, font family, color, bullets).
  - Support adding/deleting/duplicating/reordering slides, adding/moving/resizing/deleting shapes and pictures, editing table cells, and creating `notesSlide` parts when a slide lacks one.
  - Render preset/custom shape geometry (rounded rectangles, circles, arrows, callouts), rotation (`rot`), multi-stop `gradFill` gradients, picture fills (`blipFill`), alpha/opacity, arrowheads, dashed lines, native OOXML charts, SmartArt, and EMF/WMF graphics.
  - Render slide numbers/dates/headers (`sldNum`/`dt`/`hdr`), clickable links, embedded audio/video, hidden slide indicators (`show="0"`), and lettered/roman `buAutoNum` lists.
  - Upgrade `PptxSlideShowWindow` with presenter view (notes + next slide + timer), slide-number jump, `B`/`W` blackout/whiteout, laser pointer/ink, and transitions.

---

## Phase 6 — Print, Export, Save As & New Documents

Why sixth: Once viewing and editing are mature, users expect to create new documents, save copies under new names, print, and convert to PDF/CSV.

- [ ] **Save As & format conversion**:
  - **Save As…** (`Ctrl+Shift+S`) across Word, Excel, PowerPoint, and text editors.
  - Export/convert `.xlsx` → `.csv`, `.ppt` → `.pptx`, and `.docx`/`.xlsx`/`.pptx` → PDF.
- [ ] **Print support**:
  - Cross-platform/OS print dialog and silent page layout printing from PDF, Word, Excel, and PowerPoint viewers.
- [ ] **Create new documents**:
  - **New Word Document (`.docx`)**, **New Spreadsheet (`.xlsx`)**, and **New Presentation (`.pptx`)** templates in `BrowseView` ("New" menu) and `FileViewerWindow`.

---

## Phase 7 — PDF Annotations, Forms, Page Tools & OCR

Why seventh: Modifying PDF streams/objects (annotations, AcroForms, merging/splitting, and OCR engines) requires new write pipelines beyond PDFium rendering and PdfPig text reading.

- [ ] **PDF annotations & markup**:
  - Text highlight, underline, strikethrough, freehand ink, sticky notes, text boxes, and an annotations sidebar with save back to PDF.
- [ ] **Interactive forms & signatures**:
  - AcroForm field rendering, editing, and saving (text fields, checkboxes, radio buttons, dropdowns), plus digital signature verification info.
- [ ] **PDF page tools (UI & CLI `grepdesk pdf`)**:
  - Merge multiple PDFs, split / extract page ranges, reorder, delete, and persist rotated pages; optimize/compress and redact text regions.
- [ ] **OCR for scanned documents**:
  - Optional OCR backend (e.g., Windows OCR / Tesseract) for scanned PDFs to enable preview text, content search, and copyable text selection.
