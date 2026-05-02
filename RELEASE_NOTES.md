# amSetup 0.2.0.0

This release turns the generated Windows installer into a traditional setup
wizard and adds uninstall support.

Highlights:

- Traditional Windows setup wizard for generated production installers, with
  welcome, license, destination, component, shortcut, install progress, and
  finish pages.
- Theme palettes for blue, dark, amber, and oldschool installers.
- Package-loading progress plus separate overall/current-file install progress.
- Receipt-based uninstaller generation.
- Interactive shortcut tasks for desktop, Start Menu/applications menu, and
  install-folder launchers.
- Sample GUI setup installers under `artifacts/sample-setup` when built locally.
- Embedded, adjacent, and split package layouts.
- Brotli compression with speed/size options.

The generated release assets contain self-contained stubs for Windows, Linux,
and macOS. Use the matching stub for the OS and architecture you are packaging
for.
