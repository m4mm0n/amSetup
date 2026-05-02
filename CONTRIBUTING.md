# Contributing

amSetup is GPLv3-or-later software.

By contributing to this repository, you agree that:

- your contribution is your original work or you have the right to submit it;
- your contribution is licensed to the public under GPLv3 or later;
- you grant the project owner a perpetual, worldwide, non-exclusive,
  royalty-free right to use, modify, sublicense, and relicense your contribution
  as part of amSetup, including under commercial or proprietary license terms.

This contributor grant is what lets the public project remain GPL while still
letting the project owner offer separate licenses in the future.

## Development

Use .NET 10 SDK.

```powershell
dotnet build .\amSetup.slnx -c Release -warnaserror
dotnet run --no-build --project .\tests\amSetup.Validation\amSetup.Validation.csproj -c Release
dotnet format .\amSetup.slnx --verify-no-changes --no-restore
```

Build production stubs:

```powershell
.\build-stubs.ps1
```

If NativeAOT tooling is not available:

```powershell
.\build-stubs.ps1 -NoAot
```
