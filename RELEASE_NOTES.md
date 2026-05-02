# amSetup 0.2.2.0

This release finishes the Windows setup theming pass by covering the
taskbar-visible package-loading window and native window chrome.

Highlights:

- The early `Preparing Setup` package-loading window is now dark/themed instead
  of plain white.
- The loading window uses the same glossy progress style as the main setup
  wizard.
- Windows DWM caption, border, and title text colors are applied to the loading
  window, main installer wizard, and About dialog when supported by the OS.
- Previous 0.2.1.0 polish remains included: transparent PNG splash rendering,
  license preparation, `{ProgramFiles}\{Publisher}\{ProductName}` defaults, and
  the themed About dialog.
- Embedded, adjacent, and split package layouts.
- Brotli compression with speed/size options.

The generated release assets contain self-contained stubs for Windows, Linux,
and macOS. Use the matching stub for the OS and architecture you are packaging
for.
