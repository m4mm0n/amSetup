# amSetup 0.2.1.0

This release polishes the generated Windows installer and fixes packaging
details found while building the amChipper setup.

Highlights:

- Transparent PNG splash logos now render correctly in the Windows installer.
- License text is prepared during packaging with manifest token expansion,
  GPL placeholder filling, and cleaner wrapping for the agreement page.
- The default Windows install folder is now
  `{ProgramFiles}\{Publisher}\{ProductName}`.
- The installer exposes a themed About dialog from the window context/system
  menu.
- Windows themes now draw glossier sidebars, buttons, and progress bars.
- Embedded, adjacent, and split package layouts.
- Brotli compression with speed/size options.

The generated release assets contain self-contained stubs for Windows, Linux,
and macOS. Use the matching stub for the OS and architecture you are packaging
for.
