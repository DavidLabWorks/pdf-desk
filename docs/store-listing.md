# PDFDesk Microsoft Store listing draft

Use these materials when submitting PDFDesk through Partner Center. Add the
assigned Store identity, publisher and product URL after associating the app.

- **Product name:** PDFDesk
- **Repository package identity:** `DavidLabWorks.PDFDesk`
- **Repository publisher:** `CN=DavidLabWorks`
- **Publisher display name:** DavidLabWorks

## Short description (max 200 chars)

Built on .NET and the Windows App SDK, both included. A fast, free, open-source
PDF reader: instant open, smooth scrolling, forms, annotations and page editing.

*(157 chars)*

---

## Description (max 10,000 chars)

PDFDesk is built on the Microsoft .NET runtime and the Windows App SDK. Both are included in the package, so there is nothing extra to download or install.

A fast, free, open-source PDF reader for Windows that opens instantly, scrolls smoothly through thousand-page documents, and stays out of your way. No account, no subscription, no telemetry, no ads.

**Fast**

PDFDesk renders with PDFium — the same engine Chrome uses — on a dedicated thread, so scrolling never stutters and the window never freezes. Pages appear progressively: a fast preview first, then a crisp render. Large files stream from disk instead of loading into memory.

**Clean**

One slim toolbar. Everything else lives in a single menu, so the page gets the space. Tabs sit in the title bar, and the whole app follows your Windows light or dark theme.

**Complete**

• Tabs, with each document remembering where you stopped reading
• Sidebar with page thumbnails, chapters and your own bookmarks
• Text selection, copy, and find-in-document with highlight-all
• Form filling — click a field and type; text boxes, dropdowns and checkboxes all save back into the PDF, and you can set the size and colour a field fills in with so your answers stand apart from the printed form (standard AcroForm documents; XFA forms are not supported)
• Sign a document — draw your signature, type it in a handwriting style, or photograph one on paper and import it: PDFDesk removes the paper automatically, on your device, so only the ink lands on the page. Place it, move it, resize it by its corners, and reuse it next time. Signatures are stored only on your device
• Everything keeps working when you rotate the page — selecting text, highlighting, filling forms and signing all follow the rotation, so a sideways scan is as usable as any other document
• Flatten — bake annotations and filled fields into the page so they can't be edited out
• Signature details — read what a digitally signed document reports, including whether the signature covers the whole file
• Annotations — highlight, underline, strikeout, sticky notes and freehand pen — saved as standard PDF annotations that any reader can see
• Type anywhere on a page (Ctrl+T), whether or not there is anything there already: click, and a bar offers font, size, bold, italic and colour, with every change landing on the words as you make it. What you type is real PDF text rather than a picture of text, so it stays sharp at any zoom and can be searched and copied once the document is flattened
• Place a picture on a page from a PNG, JPEG, BMP, GIF or TIFF. Transparency is kept, and nothing is removed from it: a picture you chose is placed exactly as it is
• Anything you place can be picked up again. Click to select it, drag to move it, pull a corner to resize it, or press Delete. Resizing text re-renders it at the new size instead of stretching the letters
• Page editing — reorder by dragging, delete, copy and paste pages between open documents, drop a PDF into the sidebar to merge it in, or extract a selection to a new file
• Undo and redo for everything
• Presentation mode (F5) for showing slides fullscreen
• Night mode that inverts page colours for comfortable reading in the dark
• Printing with live preview and page ranges
• Full keyboard control, including a command palette

**Open source**

PDFDesk is GPLv3 and developed in the open at https://github.com/DavidLabWorks/pdf-desk-windows — read the code, file an issue, or build it yourself.

---

## Search terms (max 7, 30 chars each)

1. pdf
2. pdf reader
3. pdf viewer
4. pdf annotator
5. open source pdf
6. document reader
7. pdf editor

---

## Category

Productivity  →  (secondary: Utilities & tools)

---

## Age rating questionnaire — answers

Everything below is **No**; PDFDesk is a local document viewer.

| Question | Answer |
|---|---|
| Contains violence / sexual content / profanity / drugs | No |
| Contains user-generated content or sharing | No |
| Allows users to interact / communicate | No |
| Collects or shares personal information | No |
| Contains advertising | No |
| Contains in-app purchases or gambling | No |
| Accesses location | No |

Expected result: **everyone / 3+**.

---

## Privacy

**PDFDesk collects no data whatsoever,** and makes no network requests at all —
there is no networking code in the application. The self-updater that used to
serve the portable build was removed; the Store handles updates.

Everything PDFDesk stores stays on the device, in `%LOCALAPPDATA%\PDFDesk`:
recently-opened file paths, per-document reading positions and bookmarks, and
user preferences.

Privacy policy URL (required field — point it at the repo's policy):
`https://github.com/DavidLabWorks/pdf-desk-windows/blob/main/PRIVACY.md`

---

## Capability justification — `runFullTrust`

Partner Center flags this as a restricted capability and asks "Why do you need
the runFullTrust capability, and how will it be used in your product?".
Paste the following verbatim:

> PDFDesk is a WinUI 3 / Windows App SDK desktop application, and every
> packaged Win32 desktop app of this type runs as a full-trust process, so
> runFullTrust is required simply for the app to launch. PDFDesk needs it to render
> PDF pages via PDFium (pdfium.dll), a native C++ library called through
> P/Invoke, to read the PDF files a user explicitly opens and save annotations,
> filled form fields and signature stamps back to them, and to print. It makes
> no network connections, runs no
> background tasks, requires no elevation, modifies no system settings, and
> collects no user data. PDFDesk is open source under the GPLv3 and the full source
> is at https://github.com/DavidLabWorks/pdf-desk-windows

The "no network connections" claim is unconditional as of v0.5.0: `UpdateService`
was deleted, so no build of PDFDesk contains networking code. Adding any would
invalidate this justification and the privacy declaration together.

---

## Support / contact

- Support: https://github.com/DavidLabWorks/pdf-desk-windows/issues
- Website: https://github.com/DavidLabWorks/pdf-desk-windows

---

## Screenshots

Store requires at least one 1366×768 or larger desktop screenshot; up to 10.
The set in `docs/store-screenshots/` is now the full 10, all 1920×1080. Shots
1 to 7 were taken against v0.5.0, shots 8 to 10 against v0.6.0:

1. `01-reading-light` — light mode, thumbnails sidebar, the everyday view
2. `02-night-mode` — night mode on the same page (thumbnails invert too)
3. `03-chapters` — the chapters/TOC pane
4. `04-annotation-toolbar` — the highlighter's colour/style/opacity panel
5. `05-form-filling` — a form field focused mid-typing
6. `06-presentation` — presentation mode (F5), fullscreen
7. `07-signature` — a photographed signature imported with the paper keyed out,
   previewed over a checkerboard so the transparency is legible in a thumbnail
8. `08-page-editing` — three pages multi-selected in the thumbnail sidebar,
   showing the accent bars and the current-page ring
9. `09-shortcuts` — the F1 overlay, both columns complete (the clipping bug that
   held this shot back was fixed in `99617b8`)
10. `10-signature-resize` — a typed signature placed on the page and selected,
    with its four aspect-locked corner handles

Shot 8 is the selection state rather than the right-click menu on purpose: a
`MenuFlyout` renders in its own HWND and does not appear in a `PrintWindow`
capture, which was confirmed again when these were taken. `CopyFromScreen` is no
way around it, because this display is exactly 1920×1080 and the taskbar
overlays the bottom 60 px of any window placed at (0,0).

Capture at 1920×1080 with `PrintWindow(PW_RENDERFULLCONTENT)` (PROJECT.md `7),
windowed, using a licence-safe document. `PrintWindow` returns a solid black
bitmap if it is called before the window has finished its first composition —
wait for the app to actually draw, don't shoot immediately after launch. It is
still the right call over `Graphics.CopyFromScreen`, which cannot produce a
clean 1920×1080 on a 1080-tall display because the taskbar overlays the bottom
edge. **Shoot on an empty PDFDesk profile** — the tab strip and recents grid
otherwise leak real filenames into a commercially published image, and a saved
signature is personal data.
