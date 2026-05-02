# Dependency Analysis

The analyzer scans a payload before packaging.

It detects:

- missing `.deps.json` assets
- missing `.runtimeconfig.json` for managed apphost layouts
- framework-dependent .NET apps
- .NET Framework config markers
- Visual C++ runtime signals
- native library layout notes
- debug symbols
- very large files

When possible, it suggests manifest updates including shortcuts, components,
and `prerequisites[]`.
