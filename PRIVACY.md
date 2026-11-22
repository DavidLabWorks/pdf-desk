# Privacy Policy — PDFDesk

_Last updated: 19 November 2026_

**PDFDesk does not collect, transmit, or share any personal data.**

There is no account, no telemetry, no analytics, no advertising, and no crash
reporting service. Nothing you open in PDFDesk leaves your computer.

## What PDFDesk stores, and where

PDFDesk saves a small amount of state locally so it can pick up where you left off.
Settings and reading history are stored in `Documents\PDFDesk\PDFDesk.json`.
The default bookshelf is `Documents\PDFDesk\Library` and can be changed in
Settings. Thumbnails, signatures, clipboard copies and error logs are stored in
`%LOCALAPPDATA%\PDFDesk`.

| What | Why |
|---|---|
| Paths of recently-opened files, and their page-1 thumbnails | To show the recent-documents list |
| Per-document reading position, zoom and rotation | To reopen a document where you stopped |
| Bookmarks you create | To restore them next time you open that document |
| Your preferences (theme, sidebar, keyboard options) | To remember your settings |
| Signatures you draw or import, in `signatures\` | So you can reuse one instead of redrawing it |
| A copy of a PDF with unsaved edits that you copied to the clipboard, in `share\` | To include your edits when you paste the file |
| An error log, if something goes wrong | To help diagnose a crash |

When you copy a PDF with unsaved edits, PDFDesk attempts to remove copies in
`share\` that are more than a day old. You can delete the folder at any time.

**Your signature never leaves the device.** It is stored as an ordinary image
file in `%LOCALAPPDATA%\PDFDesk\signatures`, is never uploaded or transmitted, and
is only written into a PDF when you place it on a page yourself. You can delete
any saved signature from the Sign tool, or by deleting that folder.

When you import a photo of your signature, PDFDesk removes the paper background
itself, on your machine. No online service, no AI model and no upload is
involved — the photo is read, processed and saved without ever leaving PDFDesk.

To clear local application data, delete the files you no longer want in these
folders. The library folder can contain your own PDF copies.

## Network access

**PDFDesk makes no network requests at all.**

Opening a web link hands it to your browser.

## Your documents

PDFDesk opens PDF files from your device and renders them locally. Documents are
never uploaded, indexed, or transmitted anywhere. Annotations, filled form
fields and page edits are written back only to the file you choose to save, on
your own device.

**Form data stays in the document.** PDFDesk fills PDF forms locally and never
submits them anywhere. A PDF's own Submit button is not wired up: PDFDesk does not
execute form actions or form JavaScript, so nothing you type into a form can be
sent over the network by the document itself.

**Copy PDF File** puts the PDF on the Windows clipboard when you choose the
command. For a document with unsaved edits, PDFDesk creates a local copy that
includes your edits. You choose where to paste the file.

## Children

PDFDesk is a document viewer suitable for all ages and does not knowingly collect
information from anyone, including children.

## Changes

Any change to this policy will be committed to this repository, so the history is
public and auditable.

## Contact

Questions or concerns: https://github.com/DavidLabWorks/pdf-desk/issues
