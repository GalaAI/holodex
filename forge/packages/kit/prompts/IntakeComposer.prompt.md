Controlled content and file form inside Panel, using existing Ark fields and
buttons. Keep text editable alongside attachments so an operator can add a note.
The host persists the note with the original source and owns submission and receipts.

Infer Text, Link, Image, Video, Audio, or Document from the content; display the
recognized type instead of a selector. Use MIME type with extension fallback for
device files. Show a local image or video preview, audio controls, and a document
or link icon when a visual preview is unavailable. Never imply a pasted URL has
been fetched. Release object URLs when attachments change or the form unmounts.

The native picker and drop target accept one nonempty file per receipt with no
product byte ceiling. Keep file selection accessible on keyboard and mobile.
Disable edits only while saving; removing a file preserves the accompanying text.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. Controlled content and file form inside Panel that keeps a note editable alongside one attachment.

### Density and placement

- One nonempty file per intake receipt. (canon; IntakeComposer.prompt.md)
- Never imply a pasted URL has been fetched. (canon; IntakeComposer.prompt.md)
- Use for: Capture of a note, link or single file for a host that persists it
- Not for: Batch uploads
- Not for: Showing fetched link content

### Tokens

- `--pad-surface`: Panel inset (shared across the console)
- `--s2`: Attachment row gap (shared across the console)
- `--s3`: Preview gap (shared across the console)
- `--s4`: Form gap (shared across the console)
- `--font-body`: Note text face (shared across the console)
- `--radius-inset`: Preview and drop target corners (shared across the console)
- `--color-patina`: Drag-over outline tint (shared across the console)
- `--border-hair`: Drop target outline (shared across the console)
- `--u`: Drop target outline offset (shared across the console)

### Motion

- dragging: drop target outline fades to a patina tint, --dur-disclosure with --ease-analog; instant under reduced motion (proposed)

### Accessibility

- File selection works from a keyboard-operable Button that opens the native picker.
- The hidden file input is labeled Device file or document.
- File errors are announced with role alert.
