# amSetup

[![CI](https://github.com/m4mm0n/amSetup/actions/workflows/ci.yml/badge.svg)](https://github.com/m4mm0n/amSetup/actions/workflows/ci.yml)
[![License: GPL v3+](https://img.shields.io/badge/License-GPLv3%2B-blue.svg)](LICENSE)

`amSetup` is a cross-platform setup/installer builder for producing small installer stubs that behave like self-extracting setup packages. It is inspired by Inno Setup and InstallShield, but uses a single C#/.NET codebase and can publish native stubs for Windows, Linux, and macOS.

## What It Does

- Builds installer executables from a payload directory and JSON manifest.
- Includes a guided browser-based setup-builder UI for product details, payload selection, branding, dependency analysis, and builds.
- Produces Windows installer stubs that open as a traditional setup wizard on double-click, with welcome, license, destination, component, shortcut, install progress, and finish pages.
- Shows package-loading progress before large installers open and uses separate overall/current-file progress bars while installing.
- Includes a dependency analyzer for publish/payload folders so missing `.deps.json` assets, runtime configs, .NET/.NET Framework prerequisites, native libraries, symbols, and large split-worthy files are visible before packaging.
- Includes an oldschool default setup icon and manifest-controlled custom setup icon/splash branding.
- Supports selected install folders, interactive confirmation, silent install, dry-run, package inspection, and file listing.
- Supports embedded self-extracting installers, adjacent package archives, or split package archives.
- Uses built-in Brotli compression: `fastest`, `balanced`, `smallest`, or `store`.
- Supports install components with required/default selection.
- Creates platform-aware shortcuts for Windows, Linux, and macOS.
- Lets interactive users choose desktop, Start Menu/applications menu, and install-folder shortcut tasks.
- Applies process/user environment variables.
- Runs OS-filtered post-install actions.
- Writes install metadata to `.amsetup/install.json`.
- Creates a receipt-based uninstaller under `.amsetup` plus a launch script in the install folder.
- Verifies every extracted file with SHA-256 and blocks unsafe package paths.
- Provides installer wizard theming for header, accent, progress color, banner style, splash, and sidebar/window settings.
- Provides a small installer window designer for setup title, subtitle, intro text, footer text, size, style, and sidebar preview.
- Has no third-party runtime dependencies.

## Quick Start

```powershell
.\artifacts\stubs\win-x64\amSetup.exe
```

Double-clicking the unpackaged `amSetup.exe` does the same thing: it opens the builder UI in a browser and creates a starter project under `Documents\amSetup Projects\New Setup` if one does not exist yet. Packaged installer executables still install on double-click.

You can also choose the project file explicitly:

```powershell
dotnet run --project .\src\amSetup\amSetup.csproj -- new-project amsetup.project.json
dotnet run --project .\src\amSetup\amSetup.csproj -- builder --project amsetup.project.json
```

In the builder UI, fill the Product, Files, Branding, and Shortcut sections, run dependency analysis, then build the installer. The advanced manifest JSON remains available for precise edits.

The same workflow is available without the UI:

```powershell
dotnet run --project .\src\amSetup\amSetup.csproj -- analyze --payload .\payload --manifest .\amsetup.json --write-manifest
dotnet run --project .\src\amSetup\amSetup.csproj -- build-project --project .\amsetup.project.json
```

Direct pack mode is still available:

```powershell
dotnet run --project .\src\amSetup\amSetup.csproj -- init amsetup.json

.\artifacts\stubs\win-x64\amSetup.exe pack `
  --manifest .\amsetup.json `
  --payload .\payload `
  --output .\MySetup.exe `
  --stub .\artifacts\stubs\win-x64\amSetup.exe `
  --compression balanced `
  --layout embedded

.\MySetup.exe install
```

For production, publish one self-contained stub per runtime identifier first. See [BUILD.md](BUILD.md).

## Project Docs

- [Build and release guide](BUILD.md)
- [Changelog](CHANGELOG.md)
- [Licensing model](LICENSING.md)
- [Contributing](CONTRIBUTING.md)
- [Wiki source](docs/wiki/Home.md)

## Commands

```text
<no arguments> starts the builder UI
init [manifest.json]
new-project [amsetup.project.json]
analyze --payload <dir> [--manifest amsetup.json] [--output report.json] [--write-manifest]
build-project --project amsetup.project.json [--allow-framework-dependent-stub]
builder [--project amsetup.project.json] [--port 41873] [--no-browser]
pack --manifest amsetup.json --payload <dir> --output <installer> [--stub <exe>] [--compression fastest|balanced|smallest|store] [--layout embedded|external|split] [--chunk-size 512m]
install [--target <dir>] [--components a,b] [--silent] [--dry-run] [--list] [--console]
uninstall --target <dir> [--silent] [--dry-run]
inspect
```

## Builder UI

`amSetup builder` starts a local-only web UI at `http://127.0.0.1:41873/` by default. It is still a single C# executable: the HTML, CSS, JavaScript, dependency analyzer, manifest editor, and build calls are served from the tool itself.

The UI supports:

- project file editing
- local Browse buttons for payload folders, stub executables, icons, and splash images
- payload executable discovery for choosing the shortcut target
- payload, output, stub, compression, layout, and split chunk settings
- installer look preview before building
- installer window designer for title, subtitle, intro text, footer text, style, dimensions, and sidebar preview
- normal product fields without editing JSON first
- theme presets plus custom installer colors
- extra install-directory folders for app data, config, plugins, logs, or other runtime-relative content
- built-in oldschool setup icon selection
- custom `.ico` setup icon path
- optional launch splash image
- Windows registry edits
- manifest JSON editing
- dependency analysis with issue list and suggested manifest updates
- detected prerequisites for framework-dependent .NET apps, .NET Framework apps, and Visual C++ runtime signals
- save and build actions

Project files look like this:

```json
{
  "manifestPath": "amsetup.json",
  "payloadPath": "payload",
  "outputPath": "dist/Setup.exe",
  "stubPath": "artifacts/stubs/win-x64/amSetup.exe",
  "compression": "Balanced",
  "layout": "Embedded",
  "chunkSize": "512m"
}
```

## Dependency Analysis

`amSetup analyze` scans the payload folder before packaging. It inventories files and reports:

- `.deps.json` runtime, native, and resource assets that are referenced but not present
- managed apphost layouts with missing `.runtimeconfig.json`
- missing `.deps.json` next to detected .NET apps
- native library layout notes
- debug symbols included in the package
- very large files that should usually use split archives
- framework-dependent .NET apps that require `.NET Runtime`, `.NET Desktop Runtime`, or `ASP.NET Core Runtime`
- `.exe.config`/`.config` target framework markers that require .NET Framework
- Visual C++ runtime signals such as `vcruntime*`, `msvcp*`, and `concrt*`
- suggested components and shortcut defaults
- suggested `prerequisites[]` entries that the installer can check and run

The analyzer can write a JSON report or update the manifest with `--write-manifest`.

## Package Layouts

- `embedded`: appends the compressed package to the installer stub, producing one executable.
- `external`: writes `<InstallerName>.ampkg` beside the installer executable.
- `split`: writes `<InstallerName>.ampkg.001`, `.002`, and so on when the package is larger than `--chunk-size`.

When repacking, stale `.ampkg` and `.ampkg.*` files for the same output name are removed before new package files are written.

## Manifest Example

```json
{
  "productName": "My Application",
  "identifier": "my-application",
  "version": "1.0.0",
  "publisher": "My Company",
  "description": "My product installer",
  "defaultInstallDirectory": "{LocalAppData}\\Programs\\{ProductName}",
  "licenseText": "Optional license text.",
  "requireConfirmation": true,
  "branding": {
    "iconPath": "branding/setup.ico",
    "splashPath": "branding/splash.png",
    "showSplash": true,
    "splashDurationMilliseconds": 1500,
    "splashImageBase64": "",
    "splashContentType": ""
  },
  "theme": {
    "accentColor": "cyan",
    "headerColor": "white",
    "progressColor": "green",
    "banner": "classic"
  },
  "window": {
    "title": "{ProductName} Setup",
    "subtitle": "Install {ProductName} {Version}",
    "introText": "This setup will install {ProductName} on your computer.",
    "footerText": "Powered by amSetup",
    "style": "classic",
    "width": 720,
    "height": 460,
    "showSidebar": true
  },
  "components": [
    {
      "id": "main",
      "name": "Main Files",
      "description": "Required application files",
      "required": true,
      "defaultSelected": true,
      "include": [ "**" ]
    },
    {
      "id": "docs",
      "name": "Documentation",
      "description": "Optional documentation",
      "required": false,
      "defaultSelected": false,
      "include": [ "docs/**" ]
    }
  ],
  "installDirectories": [
    {
      "os": "any",
      "path": "data"
    },
    {
      "os": "any",
      "path": "config"
    }
  ],
  "shortcuts": [
    {
      "os": "any",
      "name": "My Application",
      "target": "{InstallDir}/MyApplication.exe",
      "arguments": "",
      "location": "desktop",
      "workingDirectory": "{InstallDir}"
    }
  ],
  "environmentVariables": [
    {
      "os": "any",
      "name": "MY_APPLICATION_HOME",
      "value": "{InstallDir}",
      "target": "process"
    }
  ],
  "registryValues": [
    {
      "os": "windows",
      "root": "HKCU",
      "key": "Software\\My Company\\My Application",
      "name": "InstallDir",
      "value": "{InstallDir}",
      "valueKind": "String",
      "ignoreFailure": false
    }
  ],
  "prerequisites": [
    {
      "id": "dotnet-desktop-runtime-10",
      "name": ".NET Desktop Runtime 10",
      "kind": "dotnet-desktop-runtime",
      "version": "10.0.0",
      "required": true,
      "bundledPath": "",
      "downloadUrl": "https://dotnet.microsoft.com/download/dotnet/10",
      "installCommand": "",
      "installArguments": "",
      "detectCommand": "",
      "detectArguments": "",
      "message": ".NET Desktop Runtime 10 is required."
    }
  ],
  "postInstall": [
    {
      "os": "windows",
      "command": "{InstallDir}/tools/register.cmd",
      "arguments": "",
      "ignoreFailure": false
    }
  ]
}
```

## Manifest Fields

Path templates support `{ProductName}`, `{Identifier}`, `{Version}`, `{Home}`, `{LocalAppData}`, `{AppData}`, `{ProgramFiles}`, and `{InstallDir}`.

`components[].include` supports exact paths, directory prefixes, `*`, `**`, and simple prefix globs like `bin/**`.

`installDirectories[]` creates empty folders inside the install directory, such as `data`, `config`, `plugins`, or `logs`. Absolute paths are only allowed when they remain inside `{InstallDir}`.

`window` controls the installer runtime text and the builder preview. Title, subtitle, intro text, and footer text support `{ProductName}`, `{Identifier}`, `{Version}`, `{Publisher}`, and `{InstallDir}`.

`shortcuts[].os`, `environmentVariables[].os`, and `postInstall[].os` accept `any`, `windows`, `linux`, or `macos`.

Shortcut locations:

- Windows: `desktop` or `startMenu`
- Linux: `desktop` or `applications`
- macOS: writes into the user's `Applications` folder
- `install`: writes the launcher into the install directory

Shortcut working directories:

- `shortcuts[].workingDirectory` controls the launch working directory.
- Use `{InstallDir}` when the app loads files relative to its install folder.
- Use `{InstallDir}/data` or another install subfolder when the app expects a specific runtime content directory.

Environment variable targets:

- `process`: available to the installer process only
- `user`: persisted for the user on Windows, appended to `~/.profile` on Linux/macOS

Branding fields:

- `branding.iconPath`: `.ico` file used by the builder and by custom published stubs. .NET single-file bundles cannot be safely resource-edited after publish, so production installer icons are baked into the stub during stub publishing.
- `branding.splashPath`: `.png` or `.jpg` file embedded into the package as the launch splash.
- `branding.showSplash`: shows the splash for interactive installs. Silent installs suppress splash display.
- `branding.splashDurationMilliseconds`: display delay before the installer continues.
- `branding.splashImageBase64` and `branding.splashContentType`: generated during packaging; you normally leave these empty.

To publish a stub with a custom Windows icon:

```powershell
.\build-stubs.ps1 -Rids win-x64 -NoAot -IconPath .\branding\setup.ico
```

Registry fields:

- `registryValues[].os`: usually `windows`; non-Windows installers skip registry edits.
- `registryValues[].root`: `HKCU`, `HKLM`, `HKCR`, `HKU`, or `HKCC`.
- `registryValues[].key`: registry key path. Templates such as `{InstallDir}` are supported.
- `registryValues[].name`: value name. Empty means default value.
- `registryValues[].value`: value data. Templates are supported.
- `registryValues[].valueKind`: `String`, `ExpandString`, `DWord`, `QWord`, `MultiString`, or `Binary`.
- `registryValues[].ignoreFailure`: continue install if the registry write fails, useful for optional HKLM writes.

Prerequisite fields:

- `prerequisites[].kind`: built-in checks support `dotnet-runtime`, `dotnet-desktop-runtime`, `aspnet-runtime`, `netfx`, and `vc-redist`.
- `prerequisites[].version`: version used by the built-in detector. .NET runtime checks match the major version.
- `prerequisites[].bundledPath`: installer executable inside the payload. Relative paths are resolved under `{InstallDir}` after extraction.
- `prerequisites[].installCommand` and `installArguments`: command to run when the prerequisite is missing.
- `prerequisites[].detectCommand` and `detectArguments`: optional custom detector. Exit code `0` means installed.
- `prerequisites[].required`: missing required prerequisites fail the install when no bundled path or install command is configured.

For .NET applications, a self-contained publish avoids requiring the user's machine to already have the matching .NET runtime. Framework-dependent publishes should keep the detected prerequisite or bundle a runtime installer.

## Runtime Behavior

An installer with an attached or adjacent package defaults to install mode. On Windows production stubs, both commands open the traditional setup wizard:

```powershell
.\MySetup.exe
.\MySetup.exe install
```

Useful runtime commands:

```powershell
.\MySetup.exe install --target "C:\Tools\MyApp" --components main,docs --silent
.\MySetup.exe install --console
.\MySetup.exe install --dry-run
.\MySetup.exe install --list
.\MySetup.exe uninstall --target "C:\Tools\MyApp"
.\MySetup.exe inspect
```

Install writes `.amsetup\install.json` plus an uninstaller executable/script. The uninstaller removes installed files, installer-created shortcuts, empty install folders, and best-effort registry values recorded in the receipt.

## Platform Model

One executable cannot run natively on every OS. `amSetup` supports every major OS by building one self-contained stub per target runtime, such as `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. The installer logic and manifest format stay the same across all stubs.

## License

amSetup is licensed under the GNU General Public License version 3 or later.
See [LICENSE](LICENSE).

GPLv3 does not forbid commercial use or selling copies. It does require
downstream recipients to keep the same GPL freedoms. The project owner retains
the option to offer separate licenses for code they own; see
[LICENSING.md](LICENSING.md).
