# Releases

Releases are created from tags:

```powershell
git tag -a v0.1.0 -m "amSetup 0.1.0"
git push origin main --tags
```

The release workflow validates the project, publishes self-contained stubs for
supported runtime identifiers, zips them, and attaches them to the GitHub
release.
