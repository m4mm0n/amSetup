# Releases

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
git tag -a v0.2.3.0 -m "amSetup 0.2.3.0"
git push origin main --tags
```

The release workflow validates the project, compiles the Windows installer
wizard target, publishes self-contained stubs for supported runtime identifiers,
zips them, and attaches them to the GitHub release.
