# amSetup 0.2.4.0

This release fixes the builder host lifecycle and adds more package
compression choices.

Highlights:

- The setup-builder web UI now has an Exit Builder button.
- `/api/shutdown` stops the local builder host, so closing the UI workflow no
  longer leaves `amSetup.exe` running until Task Manager kills it.
- New compression modes: `zlibfastest`, `zlibbalanced`, `zlibsmallest`,
  `lzma`, and `aplib`.
- Existing Brotli modes remain available as `fastest`, `balanced`, and
  `smallest`, with `store` still available for uncompressed packages.
- Validation now round-trips every supported compression mode through pack,
  inspect, install, and uninstall.
- Previous 0.2.3.0 preview work remains included: the builder preview mirrors
  the generated Windows installer wizard much more closely.

The generated release assets contain self-contained stubs for Windows, Linux,
and macOS. Use the matching stub for the OS and architecture you are packaging
for.
