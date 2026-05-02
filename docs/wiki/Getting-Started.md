# Getting Started

Run the builder:

```powershell
.\artifacts\stubs\win-x64\amSetup.exe
```

Or from source:

```powershell
dotnet run --project .\src\amSetup\amSetup.csproj -- builder
```

Basic flow:

1. Choose product name, version, publisher, and install folder.
2. Select the payload folder containing the app to install.
3. Choose the executable for shortcuts.
4. Run dependency analysis.
5. Preview the installer.
6. Build the setup executable.

Windows production installers open a traditional wizard on double-click. Use
`install --silent` for automation or `install --console` when you explicitly
want the terminal installer flow.

Installs create `.amsetup/install.json` and an uninstaller script in the install
folder. Run `uninstall --target <install-folder>` for automated removal.
