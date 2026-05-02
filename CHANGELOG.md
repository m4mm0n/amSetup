# Changelog

## 0.2.2.0 - 2026-05-02

- The package-loading taskbar window is now themed instead of using the default
  white WinForms surface.
- Applied native Windows caption, border, and title text colors to the loading
  window, installer wizard, and About dialog where supported by DWM.
- Reused glossy themed progress rendering for the early package-loading
  progress window.

## 0.2.1.0 - 2026-05-02

- Added automatic license text preparation during packaging, including manifest
  token expansion, GPL appendix placeholder filling, and cleaner paragraph
  wrapping for the Windows license page.
- Changed the default Windows install path to
  `{ProgramFiles}\{Publisher}\{ProductName}`.
- Added `{Publisher}` support to path templates.
- Added a themed About dialog through the installer window context/system menu.
- Made Windows installer themes glossier with gradient sidebars, buttons, and
  progress bars.
- Fixed transparent PNG splash rendering in the Windows installer splash window.

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
