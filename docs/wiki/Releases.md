# Releases

## 0.2.5.0

Package compression now uses the first-party `AmSetup.Compression` C# assembly.
The package path has no SevenZip, native codec DLL, SharpCompress, AuroraLib, or
BCL compression-stream dependency. The builder UI exposes Brotli, zlib, LZ4HC,
LZMA, LZMA2, aPLib, DEFLATE, GZip, XZ, and store modes, and validation
round-trips each mode through pack, inspect, install, and uninstall. Uninstall
cleanup also removes copied framework-dependent sidecars and temporary splash
files.

## 0.2.4.0

This release fixes the builder web host lifecycle and expands package
compression. The builder UI now exposes Exit Builder and a local shutdown
endpoint so the process stops cleanly. Package builds can now use Brotli,
zlib, LZMA, aPLib, or store mode, and validation round-trips each mode through
pack, inspect, install, and uninstall.

## 0.2.3.0

This release fixes the setup-builder preview so it follows the final Windows
installer UI. The preview now uses the runtime wizard frame, matching palette
rules, sidebar width, glossy buttons, bottom button bar, page titles/subtitles,
dual progress bars, and a page selector for Welcome, License, Options,
Installing, and Complete pages.

## 0.2.2.0

This release completes the setup UI theming pass. The package-loading
taskbar-visible window is now themed, the loading progress bar is glossy, and
Windows DWM caption, border, and title text colors are applied to the loading
window, main installer wizard, and About dialog where supported.

Releases are created from tags:

```powershell
git tag -a v0.2.4.0 -m "amSetup 0.2.4.0"
git push origin main --tags
```

The release workflow validates the project, compiles the Windows installer
wizard target, publishes self-contained stubs for supported runtime identifiers,
zips them, and attaches them to the GitHub release.
