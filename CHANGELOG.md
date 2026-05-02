# Changelog

## 0.2.0.0 - 2026-05-02

- Added a traditional Windows installer wizard with package-loading progress,
  welcome, license, destination, component, shortcut, install progress, and
  finish pages.
- Added installer theme palettes for blue, dark, amber, and oldschool styles,
  with builder preset wiring and themed previews.
- Added separate overall and current-file progress bars during extraction.
- Added receipt-based uninstall generation with `.amsetup\uninstall.exe` and an
  install-folder uninstall script.
- Added interactive shortcut tasks for Desktop, Start Menu/applications menu,
  and install-folder launchers.
- Added sample GUI setup projects and rebuilt sample installers.
- Updated CI/release validation to compile the Windows wizard target.

## 0.1.0 - 2026-05-02

Initial public source release.

- Cross-platform C# setup/installer builder.
- Local browser-based setup builder UI.
- Embedded, external, and split package layouts.
- Brotli compression modes.
- Install components, shortcuts, install folders, environment variables,
  registry edits, and post-install actions.
- Branding support for setup icon and launch splash.
- Installer preview and window designer.
- Dependency analysis for .NET payloads, .NET Framework markers, Visual C++
  runtime signals, missing `.deps.json` assets, missing runtime configs, native
  layout notes, symbols, and large files.
- Self-contained stub publishing for Windows, Linux, and macOS.
