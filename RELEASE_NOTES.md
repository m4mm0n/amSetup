# amSetup 0.2.5.0

This release moves package compression to first-party C# code and expands the
available package modes.

Highlights:

- Package compression now lives in `AmSetup.Compression.dll`.
- The package path no longer depends on SevenZip, native codec DLLs,
  SharpCompress, AuroraLib, or BCL compression streams.
- New compression modes: `lz4hc`, `lzma2`, `deflate`, `gzip`, and `xz`.
- Existing Brotli, zlib, LZMA, aPLib, and store modes remain available.
- The setup-builder UI exposes every supported package mode.
- Validation now round-trips every supported compression mode through pack,
  inspect, install, and uninstall.
- Uninstall cleanup now removes copied framework-dependent sidecars and temp
  splash files instead of leaving package/runtime debris behind.

The generated release assets contain self-contained stubs for Windows, Linux,
and macOS. Use the matching stub for the OS and architecture you are packaging
for.
