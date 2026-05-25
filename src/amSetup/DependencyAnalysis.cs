using System.Diagnostics;
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) ZLS
//
// amSetup
// Payload dependency analysis and prerequisite suggestion logic.

using System.Text;
using System.Text.Json;

namespace AmSetup;

internal sealed record DependencyReport(
    string PayloadPath,
    int TotalFiles,
    long TotalBytes,
    List<FileInventoryItem> Files,
    List<DotNetApplicationInfo> DotNetApplications,
    List<SetupPrerequisite> Prerequisites,
    List<DependencyIssue> Issues,
    List<SetupComponent> SuggestedComponents,
    SetupManifest SuggestedManifest);

internal sealed record FileInventoryItem(string Path, long Length, string Kind);

internal sealed record DotNetApplicationInfo(
    string Name,
    string EntryPoint,
    string? RuntimeConfig,
    string? DepsJson,
    string? Framework,
    string? FrameworkVersion,
    bool LooksSelfContained);

internal sealed record DependencyIssue(string Severity, string Code, string Message, string? Path, string? Source);

internal static class DependencyAnalyzerCommand
{
    public static int Run(string[] args)
    {
        string payload = Program.ReadOption(args, "--payload");
        string? manifestPath = TryReadOption(args, "--manifest");
        string? output = TryReadOption(args, "--output");
        bool writeManifest = args.Contains("--write-manifest", StringComparer.OrdinalIgnoreCase);

        SetupManifest? manifest = null;
        if (!string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath))
            manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath, Encoding.UTF8), AmSetupJsonContext.Default.SetupManifest);

        var report = DependencyAnalyzer.Analyze(payload, manifest);
        string json = JsonSerializer.Serialize(report, AmSetupJsonContext.Default.DependencyReport);

        if (!string.IsNullOrWhiteSpace(output))
            File.WriteAllText(output, json + Environment.NewLine, Encoding.UTF8);
        else
            Console.WriteLine(json);

        if (writeManifest)
        {
            string path = manifestPath ?? Path.Combine(Directory.GetCurrentDirectory(), "amsetup.json");
            string manifestJson = JsonSerializer.Serialize(report.SuggestedManifest, AmSetupJsonContext.Default.SetupManifest);
            File.WriteAllText(path, manifestJson + Environment.NewLine, Encoding.UTF8);
            Console.Error.WriteLine($"Wrote manifest: {path}");
        }

        return report.Issues.Any(i => i.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)) ? 2 : 0;
    }

    private static string? TryReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

internal static class DependencyAnalyzer
{
    private static readonly string[] NativeExtensions = [".dll", ".so", ".dylib"];
    private static readonly string[] ConfigExtensions = [".json", ".config", ".xml", ".ini", ".yaml", ".yml", ".toml"];

    public static DependencyReport Analyze(string payloadPath, SetupManifest? manifest = null)
    {
        string root = Path.GetFullPath(payloadPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => ToInventory(root, path))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fileSet = files.Select(f => f.Path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = files.GroupBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var issues = new List<DependencyIssue>();
        var apps = DetectDotNetApplications(root, files, fileSet, issues);
        AnalyzeDepsFiles(root, files, fileSet, byName, issues);
        var prerequisites = DetectPrerequisites(root, files, apps, issues);
        AnalyzeNativeAndContent(files, issues);

        var suggested = BuildSuggestedManifest(root, files, apps, manifest);
        var components = BuildSuggestedComponents(files);
        var mergedPrerequisites = MergePrerequisites(suggested.Prerequisites, prerequisites);

        return new DependencyReport(
            root,
            files.Count,
            files.Sum(f => f.Length),
            files,
            apps,
            mergedPrerequisites,
            issues.OrderBy(i => SeverityRank(i.Severity)).ThenBy(i => i.Code, StringComparer.Ordinal).ToList(),
            components,
            suggested with { Components = components, Prerequisites = mergedPrerequisites });
    }

    private static FileInventoryItem ToInventory(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        string ext = Path.GetExtension(path);
        string kind =
            NativeExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? "native" :
            ext.Equals(".deps.json", StringComparison.OrdinalIgnoreCase) ? "dotnet-deps" :
            ext.Equals(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ? "dotnet-runtimeconfig" :
            ConfigExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? "config" :
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ? "executable" :
            ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ? "symbols" :
            ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) ? "xml" :
            "content";
        return new FileInventoryItem(relative, new FileInfo(path).Length, kind);
    }

    private static List<DotNetApplicationInfo> DetectDotNetApplications(string root, List<FileInventoryItem> files, HashSet<string> fileSet, List<DependencyIssue> issues)
    {
        var apps = new List<DotNetApplicationInfo>();
        foreach (var runtimeConfig in files.Where(f => f.Path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
        {
            string baseRelative = runtimeConfig.Path[..^".runtimeconfig.json".Length];
            string? entry = Existing(fileSet, baseRelative + ".exe") ?? Existing(fileSet, baseRelative + ".dll") ?? runtimeConfig.Path;
            string? deps = Existing(fileSet, baseRelative + ".deps.json");
            var (framework, frameworkVersion) = ReadFramework(Path.Combine(root, runtimeConfig.Path.Replace('/', Path.DirectorySeparatorChar)));
            bool selfContained = files.Any(f => f.Path.StartsWith("host/", StringComparison.OrdinalIgnoreCase)) ||
                files.Any(f => f.Path.EndsWith("/System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase)) ||
                files.Any(f => f.Path.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));

            if (deps is null)
                issues.Add(new DependencyIssue("warning", "missing-deps-json", $"Runtime config exists for {baseRelative}, but {baseRelative}.deps.json is missing.", baseRelative + ".deps.json", runtimeConfig.Path));

            apps.Add(new DotNetApplicationInfo(Path.GetFileName(baseRelative), entry, runtimeConfig.Path, deps, framework, frameworkVersion, selfContained));
        }

        foreach (var dll in files.Where(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            string baseRelative = dll.Path[..^4];
            bool hasRuntimeConfig = fileSet.Contains(baseRelative + ".runtimeconfig.json");
            bool hasDeps = fileSet.Contains(baseRelative + ".deps.json");
            if ((hasRuntimeConfig || hasDeps) && apps.All(a => !a.EntryPoint.Equals(dll.Path, StringComparison.OrdinalIgnoreCase)))
            {
                apps.Add(new DotNetApplicationInfo(Path.GetFileName(baseRelative), dll.Path, hasRuntimeConfig ? baseRelative + ".runtimeconfig.json" : null, hasDeps ? baseRelative + ".deps.json" : null, null, null, false));
            }
        }

        foreach (var exe in files.Where(f => f.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            string baseRelative = exe.Path[..^4];
            bool hasManagedSidecar = fileSet.Contains(baseRelative + ".dll");
            if (hasManagedSidecar && !fileSet.Contains(baseRelative + ".runtimeconfig.json"))
                issues.Add(new DependencyIssue("error", "missing-runtimeconfig", $"Managed executable {exe.Path} has {baseRelative}.dll but no runtimeconfig file.", baseRelative + ".runtimeconfig.json", exe.Path));
        }

        return apps;
    }

    private static List<SetupPrerequisite> DetectPrerequisites(string root, List<FileInventoryItem> files, List<DotNetApplicationInfo> apps, List<DependencyIssue> issues)
    {
        var prerequisites = new List<SetupPrerequisite>();
        foreach (var app in apps)
        {
            if (app.LooksSelfContained) continue;
            if (string.IsNullOrWhiteSpace(app.Framework) && string.IsNullOrWhiteSpace(app.FrameworkVersion)) continue;

            string kind = app.Framework switch
            {
                "Microsoft.WindowsDesktop.App" => "dotnet-desktop-runtime",
                "Microsoft.AspNetCore.App" => "aspnet-runtime",
                _ => "dotnet-runtime"
            };
            string name = kind switch
            {
                "dotnet-desktop-runtime" => ".NET Desktop Runtime",
                "aspnet-runtime" => "ASP.NET Core Runtime",
                _ => ".NET Runtime"
            };
            string version = app.FrameworkVersion ?? "";
            prerequisites.Add(new SetupPrerequisite
            {
                Id = $"{kind}-{Major(version)}",
                Name = $"{name} {Major(version)}",
                Kind = kind,
                Version = version,
                Required = true,
                DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/" + Major(version),
                Message = $"{name} {Major(version)} is required because {app.EntryPoint} is framework-dependent."
            });
            issues.Add(new DependencyIssue("warning", "framework-dependent-dotnet", $"{app.EntryPoint} requires {name} {version}. Bundle a self-contained publish or configure this prerequisite.", app.EntryPoint, app.RuntimeConfig));
        }

        foreach (var config in files.Where(f => f.Path.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".config", StringComparison.OrdinalIgnoreCase)))
        {
            string? version = ReadNetFrameworkVersion(Path.Combine(root, config.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (version is null) continue;
            prerequisites.Add(new SetupPrerequisite
            {
                Id = "netfx-" + version,
                Name = ".NET Framework " + version,
                Kind = "netfx",
                Version = version,
                Required = true,
                DownloadUrl = "https://dotnet.microsoft.com/download/dotnet-framework",
                Message = $".NET Framework {version} is required by {config.Path}."
            });
            issues.Add(new DependencyIssue("warning", "requires-net-framework", $"{config.Path} declares .NET Framework {version}.", config.Path, config.Path));
        }

        if (files.Any(f => IsVcRuntimeSignal(f.Path)))
        {
            prerequisites.Add(new SetupPrerequisite
            {
                Id = "vc-redist-2015-2022",
                Name = "Microsoft Visual C++ Redistributable 2015-2022",
                Kind = "vc-redist",
                Version = "14",
                Required = true,
                DownloadUrl = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
                Message = "Visual C++ runtime DLLs were found. Bundle the redistributable or confirm these DLLs are carried with the app."
            });
            issues.Add(new DependencyIssue("info", "vc-runtime-detected", "Visual C++ runtime DLLs were detected. The app may need the Microsoft Visual C++ Redistributable unless those DLLs are deployed app-local.", null, null));
        }

        return prerequisites
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static void AnalyzeDepsFiles(string root, List<FileInventoryItem> files, HashSet<string> fileSet, Dictionary<string, List<FileInventoryItem>> byName, List<DependencyIssue> issues)
    {
        foreach (var deps in files.Where(f => f.Path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)))
        {
            string fullPath = Path.Combine(root, deps.Path.Replace('/', Path.DirectorySeparatorChar));
            using var doc = JsonDocument.Parse(File.ReadAllText(fullPath, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("targets", out var targets)) continue;

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    CheckAssetGroup(library.Value, "runtime", deps.Path, fileSet, byName, issues);
                    CheckAssetGroup(library.Value, "native", deps.Path, fileSet, byName, issues);
                    CheckAssetGroup(library.Value, "resources", deps.Path, fileSet, byName, issues);
                }
            }
        }
    }

    private static void CheckAssetGroup(JsonElement library, string groupName, string source, HashSet<string> fileSet, Dictionary<string, List<FileInventoryItem>> byName, List<DependencyIssue> issues)
    {
        if (!library.TryGetProperty(groupName, out var group) || group.ValueKind != JsonValueKind.Object) return;
        foreach (var asset in group.EnumerateObject())
        {
            string assetPath = asset.Name.Replace('\\', '/');
            if (assetPath.EndsWith("/_._", StringComparison.Ordinal)) continue;

            string fileName = Path.GetFileName(assetPath);
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            bool exact = fileSet.Contains(assetPath);
            bool byFileName = byName.ContainsKey(fileName);
            if (!exact && !byFileName)
            {
                string severity = groupName.Equals("native", StringComparison.OrdinalIgnoreCase) ? "error" : "warning";
                issues.Add(new DependencyIssue(severity, "missing-deps-asset", $"Referenced {groupName} asset is not present in the payload: {assetPath}", assetPath, source));
            }
        }
    }

    private static void AnalyzeNativeAndContent(List<FileInventoryItem> files, List<DependencyIssue> issues)
    {
        if (files.Any(f => f.Kind == "native") && !files.Any(f => f.Path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)))
            issues.Add(new DependencyIssue("info", "native-flat-layout", "Native libraries are present outside a runtimes/<rid>/native layout. That can be fine for a published app, but check RID-specific builds.", null, null));

        foreach (var large in files.Where(f => f.Length > 512L * 1024L * 1024L))
            issues.Add(new DependencyIssue("info", "large-file", $"Large payload file may be better with --layout split: {large.Path}", large.Path, null));

        foreach (var symbols in files.Where(f => f.Kind == "symbols"))
            issues.Add(new DependencyIssue("info", "symbols-included", $"Debug symbols are included: {symbols.Path}", symbols.Path, null));
    }

    private static SetupManifest BuildSuggestedManifest(string root, List<FileInventoryItem> files, List<DotNetApplicationInfo> apps, SetupManifest? existing)
    {
        var main = apps.FirstOrDefault() ?? GuessMainExecutable(files);
        string? product = existing?.ProductName;
        if (string.IsNullOrWhiteSpace(product))
            product = main is not null ? Path.GetFileNameWithoutExtension(main.EntryPoint) : Path.GetFileName(root);

        var manifest = (existing ?? SetupManifest.CreateDefault(product)).NormalizeForCurrentHost();
        if ((manifest.Shortcuts is null || manifest.Shortcuts.Count == 0) && main is not null)
        {
            manifest = manifest with
            {
                Shortcuts =
                [
                    new SetupShortcut
                    {
                        OS = "any",
                        Name = manifest.ProductName,
                        Target = "{InstallDir}/" + main.EntryPoint,
                        Location = "desktop"
                    }
                ]
            };
        }

        return manifest;
    }

    private static List<SetupComponent> BuildSuggestedComponents(List<FileInventoryItem> files)
    {
        var components = new List<SetupComponent>
        {
            new()
            {
                Id = "main",
                Name = "Main Files",
                Description = "Required application files and runtime dependencies",
                Required = true,
                DefaultSelected = true,
                Include = ["**"]
            }
        };

        if (files.Any(f => f.Path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith("documentation/", StringComparison.OrdinalIgnoreCase)))
        {
            components.Add(new SetupComponent
            {
                Id = "docs",
                Name = "Documentation",
                Description = "Documentation files",
                Required = false,
                DefaultSelected = false,
                Include = ["docs/**", "documentation/**"]
            });
        }

        if (files.Any(f => f.Path.StartsWith("examples/", StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith("samples/", StringComparison.OrdinalIgnoreCase)))
        {
            components.Add(new SetupComponent
            {
                Id = "examples",
                Name = "Examples",
                Description = "Example and sample files",
                Required = false,
                DefaultSelected = false,
                Include = ["examples/**", "samples/**"]
            });
        }

        return components;
    }

    private static DotNetApplicationInfo? GuessMainExecutable(List<FileInventoryItem> files)
    {
        var exe = files.FirstOrDefault(f => f.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (exe is not null) return new DotNetApplicationInfo(Path.GetFileNameWithoutExtension(exe.Path), exe.Path, null, null, null, null, false);

        var dll = files.FirstOrDefault(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && files.Any(r => r.Path.Equals(f.Path[..^4] + ".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)));
        return dll is null ? null : new DotNetApplicationInfo(Path.GetFileNameWithoutExtension(dll.Path), dll.Path, dll.Path[..^4] + ".runtimeconfig.json", dll.Path[..^4] + ".deps.json", null, null, false);
    }

    private static string? Existing(HashSet<string> fileSet, string path) => fileSet.Contains(path) ? path : null;

    private static (string? Name, string? Version) ReadFramework(string runtimeConfigPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("runtimeOptions", out var options) &&
                options.TryGetProperty("framework", out var framework) &&
                framework.TryGetProperty("name", out var name))
            {
                string? version = framework.TryGetProperty("version", out var v) ? v.GetString() : null;
                return (name.GetString(), version);
            }
        }
        catch
        {
        }

        return (null, null);
    }

    private static string? ReadNetFrameworkVersion(string configPath)
    {
        try
        {
            string text = File.ReadAllText(configPath, Encoding.UTF8);
            foreach (string marker in new[] { "targetFramework=\"", "sku=\".NETFramework,Version=v", "version=\"v" })
            {
                int start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                start += marker.Length;
                int end = text.IndexOf('"', start);
                if (end <= start) continue;
                string value = text[start..end].Trim().TrimStart('v', 'V');
                if (value.StartsWith("4.", StringComparison.Ordinal)) return value;
            }
        }
        catch
        {
        }

        return null;
    }

    private static List<SetupPrerequisite> MergePrerequisites(List<SetupPrerequisite>? existing, List<SetupPrerequisite>? suggested)
    {
        var result = new List<SetupPrerequisite>(existing ?? []);
        foreach (var item in suggested ?? [])
            if (!result.Any(p => p.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
                result.Add(item);
        return result;
    }

    private static bool IsVcRuntimeSignal(string path)
    {
        string file = Path.GetFileName(path);
        return file.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith("concrt", StringComparison.OrdinalIgnoreCase);
    }

    private static string Major(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        int dot = version.IndexOf('.');
        return dot < 0 ? version : version[..dot];
    }

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "error" => 0,
        "warning" => 1,
        "info" => 2,
        _ => 3
    };
}
