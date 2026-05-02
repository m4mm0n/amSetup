# Manifest Reference

The manifest describes the installer.

Important sections:

- `productName`, `identifier`, `version`, `publisher`
- `defaultInstallDirectory`
- `branding`
- `theme`
- `window`
- `components`
- `installDirectories`
- `shortcuts`
- `environmentVariables`
- `registryValues`
- `prerequisites`
- `postInstall`

Path templates include `{ProductName}`, `{Identifier}`, `{Version}`,
`{Publisher}`, `{Home}`, `{LocalAppData}`, `{AppData}`, `{ProgramFiles}`, and
`{InstallDir}`. On Windows, the default install folder is
`{ProgramFiles}\{Publisher}\{ProductName}`.

See the repository README for the complete example.
