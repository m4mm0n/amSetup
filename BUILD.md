# amSetup Build

`amSetup` builds framework-dependent binaries for development and self-contained single-executable stubs for production installers.

## Validate

From `E:\Repos\ZLS\amSetup`:

```powershell
dotnet build .\amSetup.slnx -c Release -warnaserror
dotnet run --no-build --project .\tests\amSetup.Validation\amSetup.Validation.csproj -c Release
dotnet format .\amSetup.slnx --verify-no-changes --no-restore
```

The validation project creates a split-archive installer, installs only the selected component into a temporary target directory, verifies extracted file contents, verifies shortcut and uninstaller creation, checks the install receipt, runs uninstall, runs `inspect` plus `install --list`, runs the dependency analyzer, builds through an `amsetup.project.json`, and smoke-tests the builder UI HTTP endpoints. Also compile the Windows wizard target before release:

```powershell
dotnet build .\src\amSetup\amSetup.csproj -c Release -warnaserror -p:UseWindowsGui=true
```

## Builder Workflow

Double-click or run the unpackaged builder executable:

```powershell
.\artifacts\stubs\win-x64\amSetup.exe
```

This starts the local builder UI and creates `Documents\amSetup Projects\New Setup\amsetup.project.json` on first launch. The UI has guided sections for product details, local Browse buttons, payload paths, packaging, dependency analysis, prerequisite review, executable shortcut selection, shortcut working directory, extra install folders, registry edits, theme presets, a small installer window designer, installer preview, icon branding, splash branding, and the advanced manifest JSON.

Create a setup-builder project:

```powershell
dotnet run --project .\src\amSetup\amSetup.csproj -- new-project .\amsetup.project.json
```

Open the UI:

```powershell
dotnet run --project .\src\amSetup\amSetup.csproj -- builder --project .\amsetup.project.json
```

Run dependency analysis without the UI:

```powershell
dotnet run --project .\src\amSetup\amSetup.csproj -- analyze --payload .\payload --manifest .\amsetup.json --output .\dependency-report.json
```

The analyzer detects framework-dependent .NET apps, .NET Framework config markers, Visual C++ runtime signals, missing `.deps.json` assets, missing runtime configs, native layout notes, symbols, and large files. Suggested prerequisites are written into `prerequisites[]` when using `--write-manifest` or the builder's Analyze button.

Build from the project file:

```powershell
.\artifacts\stubs\win-x64\amSetup.exe build-project --project .\amsetup.project.json
```

For local validation only, `build-project` accepts `--allow-framework-dependent-stub`. Production builds should use one of the published stubs below.

## Publish Production Stubs

Installer stubs should be published per target runtime identifier. The pack command rejects normal framework-dependent apphost builds unless the hidden validation-only `--allow-framework-dependent-stub` flag is used.

Small NativeAOT stubs for non-Windows targets, plus Windows GUI wizard stubs:

```powershell
.\build-stubs.ps1
```

Fallback single-file stubs when NativeAOT toolchains are unavailable. Windows stubs always use the GUI wizard build because Windows Forms is the traditional installer surface:

```powershell
.\build-stubs.ps1 -NoAot
```

Publish only selected runtimes:

```powershell
.\build-stubs.ps1 -Rids win-x64,linux-x64,osx-arm64
```

Equivalent manual NativeAOT command:

```powershell
dotnet publish .\src\amSetup\amSetup.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:StripSymbols=true -p:IlcOptimizationPreference=Size -o .\artifacts\stubs\win-x64
```

Equivalent manual single-file command:

```powershell
dotnet publish .\src\amSetup\amSetup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -o .\artifacts\stubs\win-x64
```

## Create Installers

Embedded one-file installer:

```powershell
.\artifacts\stubs\win-x64\amSetup.exe pack --manifest .\amsetup.json --payload .\payload --output .\MySetup.exe --stub .\artifacts\stubs\win-x64\amSetup.exe --compression balanced --layout embedded
```

External package beside the stub:

```powershell
.\artifacts\stubs\win-x64\amSetup.exe pack --manifest .\amsetup.json --payload .\payload --output .\MySetup.exe --stub .\artifacts\stubs\win-x64\amSetup.exe --layout external
```

Split package archives:

```powershell
.\artifacts\stubs\win-x64\amSetup.exe pack --manifest .\amsetup.json --payload .\payload --output .\MySetup.exe --stub .\artifacts\stubs\win-x64\amSetup.exe --layout split --chunk-size 512m
```

Compression modes:

- `fastest`: owned Brotli-mode fastest, best when package creation speed matters.
- `balanced`: owned Brotli-mode default ratio/speed tradeoff.
- `smallest`: owned Brotli-mode smallest size, slower but tighter.
- `zlibfastest`: owned zlib-mode fastest.
- `zlibbalanced`: owned zlib-mode default ratio/speed tradeoff.
- `zlibsmallest`: owned zlib-mode smallest size.
- `lz4hc`: owned LZ4HC block compression.
- `lzma`: owned LZMA-mode compression for tighter packages.
- `lzma2`: owned LZMA2-mode compression.
- `aplib`: owned aPLib-mode compression.
- `deflate`: owned DEFLATE-mode compression.
- `gzip`: owned GZip-mode compression.
- `xz`: owned XZ-mode compression.
- `store`: no compression.

All package compression code lives in `AmSetup.Compression.dll` and is written in C# in this repository. Production stubs do not use SevenZip, native codec DLLs, SharpCompress, AuroraLib, or BCL compression streams for package compression.

## Runtime Examples

```powershell
.\MySetup.exe install
.\MySetup.exe install --target "C:\Tools\MyApp" --components main,docs --silent
.\MySetup.exe install --console
.\MySetup.exe install --dry-run
.\MySetup.exe install --list
.\MySetup.exe uninstall --target "C:\Tools\MyApp"
.\MySetup.exe inspect
```

## Supported Runtimes

Windows production stubs open a traditional setup wizard when run interactively. The wizard lets users choose components plus desktop, Start Menu/applications menu, and install-folder shortcuts. It also shows package-loading progress before large installers open, then separate overall and current-file progress bars during extraction. Silent installs, uninstall, dry-runs, listing, inspection, and `install --console` remain command-line flows.

The same C# installer engine supports Windows, Linux, and macOS. Native execution still requires one produced stub per OS/architecture:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

The package format, manifest, component logic, UI theming, progress display, shortcuts, environment variables, and post-install actions are shared across all of them.

## Branding Assets

The repository includes:

- `assets\amsetup.ico`: oldschool default setup icon used by the builder/stub on Windows.
- `assets\amsetup-oldschool-icon-256.png`: splash-friendly PNG generated from the same icon concept.

The builder UI can insert these paths into a project. Custom setup icons should be `.ico` files. Splash images can be `.png` or `.jpg` and are embedded into the package at build time. Silent installs do not show splash images.

.NET single-file bundles should not be resource-edited after publish. To use a different Windows executable icon, publish the stub with that icon:

```powershell
.\build-stubs.ps1 -Rids win-x64 -NoAot -IconPath .\branding\setup.ico
```
