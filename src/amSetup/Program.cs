// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AuroraLib.Compression;
using AuroraLib.Compression.Formats.Common;
using SharpCompress.Compressors.LZMA;

namespace AmSetup;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var attached = PackageStore.TryOpenAttached(Environment.ProcessPath, out var package);
            if (!attached) attached = PackageStore.TryOpenExternal(Environment.ProcessPath, out package);
            if (attached && args.Length == 0)
                return Installer.Run(package!, args.Length > 0 ? args[1..] : args);

            if (args.Length == 0) return SetupBuilderUi.Run(args);
            return args[0].ToLowerInvariant() switch
            {
                "init" => Init(args[1..]),
                "new-project" => BuilderProjectCommands.New(args[1..]),
                "analyze" => DependencyAnalyzerCommand.Run(args[1..]),
                "build-project" => BuilderProjectCommands.Build(args[1..]),
                "builder" => SetupBuilderUi.Run(args[1..]),
                "pack" => Pack(args[1..]),
                "install" => attached ? Installer.Run(package!, args[1..]) : throw new InvalidOperationException("This executable has no attached or adjacent amSetup package."),
                "uninstall" => Uninstaller.Run(args[1..]),
                "inspect" => attached ? Inspector.Run(package!) : throw new InvalidOperationException("This executable has no attached or adjacent amSetup package."),
                _ when attached && IsInstallCommand(args[0]) => Installer.Run(package!, args),
                "help" or "--help" or "-h" => Help(),
                _ => Help()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("amSetup: " + ex.Message);
            return 1;
        }
    }

    private static bool IsInstallCommand(string arg) =>
        arg.Equals("install", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--target", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--components", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--console", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--list", StringComparison.OrdinalIgnoreCase);

    private static int Init(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "amsetup.json";
        if (File.Exists(path)) throw new IOException($"Manifest already exists: {path}");
        var manifest = SetupManifest.CreateDefault("My Application");
        var json = JsonSerializer.Serialize(manifest, AmSetupJsonContext.Default.SetupManifest);
        File.WriteAllText(path, json + Environment.NewLine, Encoding.UTF8);
        Console.WriteLine($"Created {path}");
        return 0;
    }

    private static int Pack(string[] args)
    {
        string? manifestPath = null;
        string? payloadPath = null;
        string? outputPath = null;
        string? stubPath = null;
        CompressionModeName compression = CompressionModeName.Balanced;
        bool allowFrameworkStub = false;
        PackageLayout layout = PackageLayout.Embedded;
        long chunkSize = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-m" or "--manifest":
                    manifestPath = RequireValue(args, ref i);
                    break;
                case "-p" or "--payload":
                    payloadPath = RequireValue(args, ref i);
                    break;
                case "-o" or "--output":
                    outputPath = RequireValue(args, ref i);
                    break;
                case "--stub":
                    stubPath = RequireValue(args, ref i);
                    break;
                case "--compression":
                    compression = Enum.Parse<CompressionModeName>(RequireValue(args, ref i), ignoreCase: true);
                    break;
                case "--layout":
                    layout = Enum.Parse<PackageLayout>(RequireValue(args, ref i), ignoreCase: true);
                    break;
                case "--chunk-size":
                    chunkSize = SizeParser.Parse(RequireValue(args, ref i));
                    break;
                case "--allow-framework-dependent-stub":
                    allowFrameworkStub = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown pack option: {args[i]}");
            }
        }

        if (manifestPath is null) throw new ArgumentException("Missing --manifest <file>.");
        if (payloadPath is null) throw new ArgumentException("Missing --payload <directory>.");
        if (outputPath is null) throw new ArgumentException("Missing --output <installer>.");
        if (!Directory.Exists(payloadPath)) throw new DirectoryNotFoundException(payloadPath);

        stubPath ??= ResolveDefaultStub();
        if (!File.Exists(stubPath)) throw new FileNotFoundException("Stub executable not found. Publish amSetup first or pass --stub.", stubPath);
        if (!allowFrameworkStub) ValidateSingleExecutableStub(stubPath);

        var manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath, Encoding.UTF8), AmSetupJsonContext.Default.SetupManifest)
            ?? throw new InvalidOperationException("Manifest could not be read.");
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
        manifest = BrandingProcessor.PrepareForPackage(manifest.NormalizeForCurrentHost(), manifestDirectory);

        PackageBuilder.WriteInstaller(stubPath, outputPath, payloadPath, manifest, compression, layout, chunkSize);
        Console.WriteLine($"Created installer: {outputPath}");
        return 0;
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (++index >= args.Length) throw new ArgumentException($"Missing value for {args[index - 1]}.");
        return args[index];
    }

    private static string ResolveDefaultStub()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        string baseDir = AppContext.BaseDirectory;
        string exeName = OperatingSystem.IsWindows() ? "amSetup.exe" : "amSetup";
        string candidate = Path.Combine(baseDir, exeName);
        if (File.Exists(candidate)) return candidate;

        throw new FileNotFoundException("Could not resolve a native amSetup stub. Publish first or pass --stub.");
    }

    private static void ValidateSingleExecutableStub(string stubPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(stubPath)) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(stubPath);
        string[] sidecars =
        [
            Path.Combine(directory, baseName + ".dll"),
            Path.Combine(directory, baseName + ".deps.json"),
            Path.Combine(directory, baseName + ".runtimeconfig.json")
        ];

        if (sidecars.Any(File.Exists))
            throw new InvalidOperationException("The stub must be a published single executable, not the framework-dependent build apphost. Publish with --self-contained true and PublishSingleFile/PublishAot, then pass that executable as --stub.");
    }

    private static int Help()
    {
        Console.WriteLine("amSetup - cross-platform single-executable installer builder");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  init [manifest.json]");
        Console.WriteLine("  new-project [amsetup.project.json]");
        Console.WriteLine("  analyze --payload <dir> [--manifest amsetup.json] [--output report.json] [--write-manifest]");
        Console.WriteLine("  build-project --project amsetup.project.json [--allow-framework-dependent-stub]");
        Console.WriteLine("  builder [--project amsetup.project.json] [--port 41873] [--no-browser]");
        Console.WriteLine("  pack --manifest amsetup.json --payload <dir> --output <installer> [--stub <exe>] [--compression fastest|balanced|smallest|store|zlibfastest|zlibbalanced|zlibsmallest|lzma|aplib] [--layout embedded|external|split] [--chunk-size 512m]");
        Console.WriteLine("  install [--target <dir>] [--components a,b] [--silent] [--dry-run] [--list] [--console]");
        Console.WriteLine("  uninstall --target <dir> [--silent] [--dry-run]");
        Console.WriteLine("  inspect");
        return 0;
    }

    internal static string ReadOption(string[] args, string name, string? fallback = null)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        if (fallback is not null) return fallback;
        throw new ArgumentException($"Missing {name} <value>.");
    }

    internal static string ResolveDefaultStubPath() => ResolveDefaultStub();

    internal static void EnsureProductionStub(string stubPath, bool allowFrameworkStub)
    {
        if (!allowFrameworkStub) ValidateSingleExecutableStub(stubPath);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<CompressionModeName>))]
internal enum CompressionModeName
{
    Store = 0,
    Fastest = 1,
    Balanced = 2,
    Smallest = 3,
    ZLibFastest = 10,
    ZLibBalanced = 11,
    ZLibSmallest = 12,
    Lzma = 20,
    Aplib = 30
}

[JsonConverter(typeof(JsonStringEnumConverter<PackageLayout>))]
internal enum PackageLayout
{
    Embedded,
    External,
    Split
}

internal sealed record SetupManifest
{
    public string ProductName { get; init; } = "";
    public string Identifier { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Publisher { get; init; } = "";
    public string Description { get; init; } = "";
    public string DefaultInstallDirectory { get; init; } = "";
    public string? LicenseText { get; init; }
    public bool RequireConfirmation { get; init; } = true;
    public SetupBranding Branding { get; init; } = new();
    public SetupTheme Theme { get; init; } = new();
    public SetupWindow Window { get; init; } = new();
    public List<SetupComponent> Components { get; init; } = [];
    public List<SetupInstallDirectory> InstallDirectories { get; init; } = [];
    public List<SetupShortcut> Shortcuts { get; init; } = [];
    public List<SetupEnvironmentVariable> EnvironmentVariables { get; init; } = [];
    public List<SetupRegistryValue> RegistryValues { get; init; } = [];
    public List<SetupPrerequisite> Prerequisites { get; init; } = [];
    public List<SetupAction> PostInstall { get; init; } = [];

    public static SetupManifest CreateDefault(string productName)
    {
        string identifier = productName.ToLowerInvariant().Replace(' ', '-');
        return new SetupManifest
        {
            ProductName = productName,
            Identifier = identifier,
            Version = "1.0.0",
            Publisher = "Your Company",
            Description = "amSetup package",
            DefaultInstallDirectory = PlatformDefaults.InstallDirectoryTemplate("Your Company", identifier, productName),
            LicenseText = "Replace this text with your license. Leave empty to skip.",
            RequireConfirmation = true,
            Branding = new SetupBranding
            {
                IconPath = "",
                SplashPath = "",
                SplashDurationMilliseconds = 1500,
                ShowSplash = false
            },
            Theme = new SetupTheme
            {
                AccentColor = "cyan",
                HeaderColor = "white",
                ProgressColor = "green",
                Banner = "classic"
            },
            Window = new SetupWindow
            {
                Title = "{ProductName} Setup",
                Subtitle = "Install {ProductName} {Version}",
                IntroText = "This setup will install {ProductName} on your computer.",
                FooterText = "Powered by amSetup",
                Style = "classic",
                Width = 720,
                Height = 460,
                ShowSidebar = true
            },
            Components =
            [
                new SetupComponent
                {
                    Id = "main",
                    Name = "Main Files",
                    Description = "Required application files",
                    Required = true,
                    DefaultSelected = true,
                    Include = ["**"]
                }
            ]
        };
    }

    public SetupManifest NormalizeForCurrentHost()
    {
        string product = string.IsNullOrWhiteSpace(ProductName) ? "Application" : ProductName.Trim();
        string id = string.IsNullOrWhiteSpace(Identifier) ? product.ToLowerInvariant().Replace(' ', '-') : Identifier.Trim();
        string publisher = string.IsNullOrWhiteSpace(Publisher) ? "Your Company" : Publisher.Trim();
        string dir = string.IsNullOrWhiteSpace(DefaultInstallDirectory) || IsLegacyPerUserInstallDirectory(DefaultInstallDirectory)
            ? PlatformDefaults.InstallDirectoryTemplate(publisher, id, product)
            : DefaultInstallDirectory;

        return this with { ProductName = product, Identifier = id, Publisher = publisher, DefaultInstallDirectory = dir };
    }

    private static bool IsLegacyPerUserInstallDirectory(string value) =>
        value.Equals("{LocalAppData}\\Programs\\{ProductName}", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("{LocalAppData}/Programs/{ProductName}", StringComparison.OrdinalIgnoreCase);
}

internal sealed record SetupAction
{
    public string OS { get; init; } = "any";
    public string Command { get; init; } = "";
    public string Arguments { get; init; } = "";
    public bool IgnoreFailure { get; init; }
}

internal sealed record SetupBranding
{
    public string IconPath { get; init; } = "";
    public string SplashPath { get; init; } = "";
    public bool ShowSplash { get; init; }
    public int SplashDurationMilliseconds { get; init; } = 1500;
    public string SplashImageBase64 { get; init; } = "";
    public string SplashContentType { get; init; } = "";
}

internal sealed record SetupTheme
{
    public string AccentColor { get; init; } = "cyan";
    public string HeaderColor { get; init; } = "white";
    public string ProgressColor { get; init; } = "green";
    public string Banner { get; init; } = "classic";
}

internal sealed record SetupWindow
{
    public string Title { get; init; } = "{ProductName} Setup";
    public string Subtitle { get; init; } = "Install {ProductName} {Version}";
    public string IntroText { get; init; } = "This setup will install {ProductName} on your computer.";
    public string FooterText { get; init; } = "Powered by amSetup";
    public string Style { get; init; } = "classic";
    public int Width { get; init; } = 720;
    public int Height { get; init; } = 460;
    public bool ShowSidebar { get; init; } = true;
}

internal sealed record SetupComponent
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Required { get; init; }
    public bool DefaultSelected { get; init; } = true;
    public List<string> Include { get; init; } = [];
}

internal sealed record SetupShortcut
{
    public string OS { get; init; } = "any";
    public string Name { get; init; } = "";
    public string Target { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string Location { get; init; } = "desktop";
    public string WorkingDirectory { get; init; } = "{InstallDir}";
}

internal sealed record SetupInstallDirectory
{
    public string OS { get; init; } = "any";
    public string Path { get; init; } = "";
}

internal sealed record SetupEnvironmentVariable
{
    public string OS { get; init; } = "any";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Target { get; init; } = "process";
}

internal sealed record SetupRegistryValue
{
    public string OS { get; init; } = "windows";
    public string Root { get; init; } = "HKCU";
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string ValueKind { get; init; } = "String";
    public bool IgnoreFailure { get; init; }
}

internal sealed record SetupPrerequisite
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Required { get; init; } = true;
    public string BundledPath { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string InstallCommand { get; init; } = "";
    public string InstallArguments { get; init; } = "";
    public string DetectCommand { get; init; } = "";
    public string DetectArguments { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed record PackageInfo(
    SetupManifest Manifest,
    CompressionModeName Compression,
    List<PackageFile> Files,
    long UncompressedBytes,
    long CompressedBytes,
    List<InstalledArtifact>? Artifacts = null);

internal sealed record PackageFile(string Path, long Length, string Sha256, string ComponentId);

internal sealed record InstalledArtifact(string Kind, string Path, string Root = "", string Key = "", string Name = "");
internal sealed record InstallOptions(List<SetupShortcut>? Shortcuts = null);

internal static class PackageStore
{
    private static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("AMSETUP1");
    private static readonly byte[] PackageMagic = Encoding.ASCII.GetBytes("AMSPKG1\n");
    private const int FooterSize = 16;

    public static bool TryOpenAttached(string? executablePath, out AttachedPackage? package)
    {
        package = null;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return false;

        using var stream = File.OpenRead(executablePath);
        if (stream.Length < FooterSize) return false;
        stream.Position = stream.Length - FooterSize;
        Span<byte> footer = stackalloc byte[FooterSize];
        stream.ReadExactly(footer);
        if (!footer[8..].SequenceEqual(FooterMagic)) return false;
        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(footer[..8]);
        if (payloadLength <= 0 || payloadLength > stream.Length - FooterSize) return false;
        package = AttachedPackage.Embedded(executablePath, stream.Length - FooterSize - payloadLength, payloadLength);
        return true;
    }

    public static bool TryOpenExternal(string? executablePath, out AttachedPackage? package)
    {
        package = null;
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        string directory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(executablePath);
        string single = Path.Combine(directory, baseName + ".ampkg");
        if (File.Exists(single))
        {
            package = AttachedPackage.External([single]);
            return true;
        }

        var chunks = Directory.EnumerateFiles(directory, baseName + ".ampkg.*")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (chunks.Count == 0) return false;
        package = AttachedPackage.External(chunks);
        return true;
    }

    public static void AppendPackage(string stubPath, string outputPath, byte[] packageBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        File.Copy(stubPath, outputPath, overwrite: true);
        using var output = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.None);
        output.Write(packageBytes);
        Span<byte> footer = stackalloc byte[FooterSize];
        BinaryPrimitives.WriteInt64LittleEndian(footer[..8], packageBytes.Length);
        FooterMagic.CopyTo(footer[8..]);
        output.Write(footer);
    }

    public static void CopyStub(string stubPath, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        File.Copy(stubPath, outputPath, overwrite: true);
    }

    public static byte[] CreatePackageBytes(string payloadRoot, SetupManifest manifest, CompressionModeName compression)
    {
        using var raw = new MemoryStream();
        raw.Write(PackageMagic);
        WriteJson(raw, manifest);

        var files = Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        WriteInt32(raw, files.Count);

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(payloadRoot, file).Replace('\\', '/');
            string componentId = ComponentMatcher.ComponentFor(relative, manifest);
            byte[] pathBytes = Encoding.UTF8.GetBytes(relative);
            byte[] componentBytes = Encoding.UTF8.GetBytes(componentId);
            byte[] content = File.ReadAllBytes(file);
            byte[] hash = SHA256.HashData(content);
            WriteInt32(raw, pathBytes.Length);
            raw.Write(pathBytes);
            WriteInt64(raw, content.Length);
            raw.Write(hash);
            WriteInt32(raw, componentBytes.Length);
            raw.Write(componentBytes);
            raw.Write(content);
        }

        return Compress(raw.ToArray(), compression);
    }

    public static PackageArchive ReadPackage(AttachedPackage package, Action<PackageLoadProgress>? progress = null)
    {
        byte[] compressed;
        if (package.IsExternal)
        {
            using var output = new MemoryStream();
            long done = 0;
            long total = package.PayloadLength;
            foreach (string part in package.ExternalParts)
            {
                using var input = File.OpenRead(part);
                CopyWithProgress(input, output, b =>
                {
                    done += b;
                    progress?.Invoke(new PackageLoadProgress("Loading package archives", done, total));
                });
            }
            compressed = output.ToArray();
        }
        else
        {
            compressed = new byte[package.PayloadLength];
            using var file = File.OpenRead(package.ExecutablePath);
            file.Position = package.PayloadOffset;
            int offset = 0;
            while (offset < compressed.Length)
            {
                int read = file.Read(compressed, offset, Math.Min(1024 * 1024, compressed.Length - offset));
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
                progress?.Invoke(new PackageLoadProgress("Loading embedded package", offset, compressed.Length));
            }
        }

        var (raw, compression) = Decompress(compressed, progress);
        progress?.Invoke(new PackageLoadProgress("Reading package manifest", 95, 100));
        using var stream = new MemoryStream(raw);
        Span<byte> magic = stackalloc byte[PackageMagic.Length];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(PackageMagic)) throw new InvalidDataException("Invalid amSetup package magic.");

        var manifest = ReadJson<SetupManifest>(stream, AmSetupJsonContext.Default.SetupManifest);
        int count = ReadInt32(stream);
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("Invalid package file count.");
        var entries = new List<PackageEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int pathLen = ReadInt32(stream);
            if (pathLen <= 0 || pathLen > 32 * 1024) throw new InvalidDataException("Invalid package path length.");
            byte[] pathBytes = new byte[pathLen];
            stream.ReadExactly(pathBytes);
            string path = Encoding.UTF8.GetString(pathBytes);
            long length = ReadInt64(stream);
            if (length < 0 || length > stream.Length - stream.Position) throw new InvalidDataException($"Invalid package entry length for {path}.");
            byte[] sha = new byte[32];
            stream.ReadExactly(sha);
            int componentLen = ReadInt32(stream);
            if (componentLen < 0 || componentLen > 8 * 1024) throw new InvalidDataException("Invalid package component length.");
            byte[] componentBytes = new byte[componentLen];
            stream.ReadExactly(componentBytes);
            string componentId = Encoding.UTF8.GetString(componentBytes);
            long offset = stream.Position;
            stream.Position += length;
            entries.Add(new PackageEntry(path, offset, length, Convert.ToHexString(sha).ToLowerInvariant(), componentId));
        }

        progress?.Invoke(new PackageLoadProgress("Package ready", 100, 100));
        return new PackageArchive(manifest, raw, entries, compressed.Length, compression);
    }

    private static void CopyWithProgress(Stream input, Stream output, Action<int> copied)
    {
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            copied(read);
        }
    }

    private static void WriteJson<T>(Stream stream, T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), AmSetupJsonContext.Default);
        WriteInt32(stream, json.Length);
        stream.Write(json);
    }

    private static T ReadJson<T>(Stream stream, JsonTypeInfo<T> info)
    {
        int len = ReadInt32(stream);
        byte[] json = new byte[len];
        stream.ReadExactly(json);
        return JsonSerializer.Deserialize(json, info) ?? throw new InvalidDataException("Package JSON could not be read.");
    }

    private static byte[] Compress(byte[] raw, CompressionModeName mode)
    {
        using var output = new MemoryStream();
        output.WriteByte((byte)mode);
        if (mode == CompressionModeName.Store)
        {
            output.Write(raw);
            return output.ToArray();
        }

        if (IsBrotliMode(mode))
        {
            using (var brotli = new BrotliStream(output, CompressionLevelFor(mode), leaveOpen: true))
                brotli.Write(raw);
            return output.ToArray();
        }

        switch (mode)
        {
            case CompressionModeName.ZLibFastest:
            case CompressionModeName.ZLibBalanced:
            case CompressionModeName.ZLibSmallest:
                using (var zlib = new ZLibStream(output, CompressionLevelFor(mode), leaveOpen: true))
                    zlib.Write(raw);
                break;
            case CompressionModeName.Lzma:
                using (var lzip = new LZipStream(output, SharpCompress.Compressors.CompressionMode.Compress, leaveOpen: true))
                {
                    lzip.Write(raw, 0, raw.Length);
                    lzip.Finish();
                }
                break;
            case CompressionModeName.Aplib:
                new aPLib().Compress(raw, output, AuroraSettings(mode));
                break;
            default:
                throw new InvalidDataException("Unknown amSetup compression mode.");
        }

        return output.ToArray();
    }

    private static (byte[] Raw, CompressionModeName Mode) Decompress(byte[] data, Action<PackageLoadProgress>? progress = null)
    {
        if (data.Length == 0) throw new InvalidDataException("Empty package.");
        var mode = (CompressionModeName)data[0];
        if (mode == CompressionModeName.Store)
        {
            progress?.Invoke(new PackageLoadProgress("Preparing stored package", 100, 100));
            return (data[1..], mode);
        }
        if (!Enum.IsDefined(mode)) throw new InvalidDataException("Unknown amSetup compression mode.");

        using var input = new MemoryStream(data, 1, data.Length - 1);
        using var output = new MemoryStream();
        if (IsBrotliMode(mode))
        {
            using var brotli = new BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
            CopyDecompressed(brotli, output, input, progress);
            return (output.ToArray(), mode);
        }

        switch (mode)
        {
            case CompressionModeName.ZLibFastest:
            case CompressionModeName.ZLibBalanced:
            case CompressionModeName.ZLibSmallest:
                using (var zlib = new ZLibStream(input, System.IO.Compression.CompressionMode.Decompress))
                    CopyDecompressed(zlib, output, input, progress);
                break;
            case CompressionModeName.Lzma:
                using (var lzip = new LZipStream(input, SharpCompress.Compressors.CompressionMode.Decompress))
                    CopyDecompressed(lzip, output, input, progress);
                break;
            case CompressionModeName.Aplib:
                new aPLib().Decompress(input, output);
                progress?.Invoke(new PackageLoadProgress("Unpacking package to memory", input.Length, input.Length));
                break;
            default:
                throw new InvalidDataException("Unknown amSetup compression mode.");
        }

        return (output.ToArray(), mode);
    }

    private static bool IsBrotliMode(CompressionModeName mode) =>
        mode is CompressionModeName.Fastest or CompressionModeName.Balanced or CompressionModeName.Smallest;

    private static CompressionLevel CompressionLevelFor(CompressionModeName mode) => mode switch
    {
        CompressionModeName.Fastest or CompressionModeName.ZLibFastest => CompressionLevel.Fastest,
        CompressionModeName.Smallest or CompressionModeName.ZLibSmallest => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal
    };

    private static CompressionSettings AuroraSettings(CompressionModeName mode) => CompressionLevelFor(mode);

    private static void CopyDecompressed(Stream source, MemoryStream output, Stream progressStream, Action<PackageLoadProgress>? progress)
    {
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            progress?.Invoke(new PackageLoadProgress("Unpacking package to memory", progressStream.Position, progressStream.Length));
        }
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        stream.Write(b);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> b = stackalloc byte[4];
        stream.ReadExactly(b);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        stream.Write(b);
    }

    private static long ReadInt64(Stream stream)
    {
        Span<byte> b = stackalloc byte[8];
        stream.ReadExactly(b);
        return BinaryPrimitives.ReadInt64LittleEndian(b);
    }
}

internal sealed record AttachedPackage(string ExecutablePath, long PayloadOffset, long PayloadLength, List<string> ExternalParts)
{
    public bool IsExternal => ExternalParts.Count > 0;
    public static AttachedPackage Embedded(string path, long offset, long length) => new(path, offset, length, []);
    public static AttachedPackage External(List<string> parts) => new("", 0, parts.Sum(p => new FileInfo(p).Length), parts);
}

internal sealed record PackageEntry(string Path, long Offset, long Length, string Sha256, string ComponentId);
internal sealed record PackageArchive(SetupManifest Manifest, byte[] Raw, List<PackageEntry> Entries, long CompressedBytes, CompressionModeName Compression);
internal sealed record PackageLoadProgress(string Stage, long Done, long Total);

internal static class PackageBuilder
{
    public static void WriteInstaller(string stubPath, string outputPath, string payloadRoot, SetupManifest manifest, CompressionModeName compression, PackageLayout layout, long chunkSize)
    {
        byte[] package = PackageStore.CreatePackageBytes(payloadRoot, manifest, compression);
        DeleteExternalPackages(outputPath);

        if (layout == PackageLayout.Embedded)
        {
            PackageStore.AppendPackage(stubPath, outputPath, package);
            return;
        }

        PackageStore.CopyStub(stubPath, outputPath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(outputPath);
        if (layout == PackageLayout.External || chunkSize <= 0 || package.Length <= chunkSize)
        {
            File.WriteAllBytes(Path.Combine(directory, baseName + ".ampkg"), package);
            return;
        }

        int index = 1;
        for (long offset = 0; offset < package.Length; offset += chunkSize)
        {
            int len = (int)Math.Min(chunkSize, package.Length - offset);
            string part = Path.Combine(directory, $"{baseName}.ampkg.{index:000}");
            File.WriteAllBytes(part, package.AsSpan((int)offset, len).ToArray());
            index++;
        }
    }

    private static void DeleteExternalPackages(string outputPath)
    {
        string fullOutput = Path.GetFullPath(outputPath);
        string directory = Path.GetDirectoryName(fullOutput) ?? ".";
        Directory.CreateDirectory(directory);
        string baseName = Path.GetFileNameWithoutExtension(fullOutput);

        string single = Path.Combine(directory, baseName + ".ampkg");
        if (File.Exists(single)) File.Delete(single);

        foreach (string part in Directory.EnumerateFiles(directory, baseName + ".ampkg.*"))
            File.Delete(part);
    }
}

internal static class Installer
{
    public static int Run(AttachedPackage attached, string[] args)
    {
        bool silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        bool list = args.Contains("--list", StringComparer.OrdinalIgnoreCase);
        bool forceConsole = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
        string? target = ValueAfter(args, "--target");
        string? componentArg = ValueAfter(args, "--components");

        if (!forceConsole && !silent && !dryRun && !list && InstallerGui.TryRun(attached, target, componentArg, out int guiExitCode))
            return guiExitCode;

        var archive = PackageStore.ReadPackage(attached);
        if (list)
        {
            foreach (var entry in archive.Entries) Console.WriteLine($"{entry.Length,10}  {entry.Path}");
            return 0;
        }

        target ??= PathTemplate.Expand(archive.Manifest.DefaultInstallDirectory, archive.Manifest);
        var ui = new ConsoleInstallerUi(archive.Manifest.Theme);
        if (!silent) InstallerSplash.Show(archive.Manifest);
        var window = archive.Manifest.Window ?? new SetupWindow();
        string title = ExpandWindowText(string.IsNullOrWhiteSpace(window.Title) ? "{ProductName} {Version}" : window.Title, archive.Manifest, target);
        TrySetConsoleTitle(title);
        ui.Header(title);
        WriteOptionalLine(ExpandWindowText(window.Subtitle, archive.Manifest, target), ui);
        WriteOptionalLine(ExpandWindowText(window.IntroText, archive.Manifest, target));
        Console.WriteLine($"Default target: {target}");
        Console.WriteLine($"Files: {archive.Entries.Count}, compressed package: {archive.CompressedBytes:N0} bytes");

        if (!silent && archive.Manifest.RequireConfirmation)
        {
            Console.Write($"Install folder [{target}]: ");
            string? selectedTarget = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(selectedTarget)) target = selectedTarget.Trim();

            string? licenseText = LicenseTextProcessor.PrepareForPackage(archive.Manifest);
            if (!string.IsNullOrWhiteSpace(licenseText))
            {
                Console.WriteLine();
                Console.WriteLine(licenseText);
            }

            Console.Write("Install? [Y/n] ");
            string? answer = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(answer) && answer.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
                return 2;
        }

        var selectedComponents = ResolveComponents(archive.Manifest, componentArg, silent);
        int result = InstallSelected(archive, target, selectedComponents, dryRun, silent, ui);
        if (dryRun) return result;
        WriteOptionalLine(ExpandWindowText(window.FooterText, archive.Manifest, target), ui);
        Console.WriteLine("Install complete.");
        return result;
    }

    private static void TrySetConsoleTitle(string title)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(title)) Console.Title = title;
        }
        catch
        {
        }
    }

    private static void WriteOptionalLine(string text, IInstallerUi? ui = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (ui is not null)
        {
            ui.Line(text);
            return;
        }

        Console.WriteLine(text);
    }

    internal static string ExpandWindowText(string template, SetupManifest manifest, string? target = null)
    {
        string result = template
            .Replace("{ProductName}", manifest.ProductName, StringComparison.OrdinalIgnoreCase)
            .Replace("{Identifier}", manifest.Identifier, StringComparison.OrdinalIgnoreCase)
            .Replace("{Version}", manifest.Version, StringComparison.OrdinalIgnoreCase)
            .Replace("{Publisher}", manifest.Publisher, StringComparison.OrdinalIgnoreCase)
            .Replace("{InstallDir}", target ?? "", StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(result);
    }

    internal static List<SetupComponent> ComponentsForDisplay(SetupManifest manifest)
    {
        var manifestComponents = manifest.Components ?? [];
        return manifestComponents.Count == 0
            ? [new SetupComponent { Id = "main", Name = "Main Files", Description = "Required application files", Required = true, DefaultSelected = true, Include = ["**"] }]
            : manifestComponents;
    }

    internal static HashSet<string> ResolveComponents(SetupManifest manifest, string? componentArg, bool silent)
    {
        var components = ComponentsForDisplay(manifest);

        if (!string.IsNullOrWhiteSpace(componentArg))
        {
            var explicitSelection = componentArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var required in components.Where(c => c.Required))
                explicitSelection.Add(required.Id);
            return explicitSelection;
        }

        var selected = components.Where(c => c.Required || c.DefaultSelected).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (silent) return selected;

        Console.WriteLine();
        Console.WriteLine("Components:");
        foreach (var component in components)
        {
            if (component.Required)
            {
                Console.WriteLine($"  [x] {component.Name} (required)");
                continue;
            }

            Console.Write($"  Install {component.Name}? [{(component.DefaultSelected ? "Y/n" : "y/N")}] ");
            string? answer = Console.ReadLine();
            bool choose = string.IsNullOrWhiteSpace(answer) ? component.DefaultSelected : answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
            if (choose) selected.Add(component.Id);
            else selected.Remove(component.Id);
        }

        return selected;
    }

    internal static int InstallSelected(PackageArchive archive, string target, HashSet<string> selectedComponents, bool dryRun, bool silent, IInstallerUi ui, InstallOptions? options = null)
    {
        var selectedEntries = archive.Entries
            .Where(e => selectedComponents.Contains(e.ComponentId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (dryRun)
        {
            ui.Line("Dry run complete. No files were written.");
            ui.Line($"Selected files: {selectedEntries.Count}");
            return 0;
        }

        var artifacts = new List<InstalledArtifact>();
        ExtractArchive(archive, selectedEntries, target, ui, artifacts);
        CreateInstallDirectories(archive.Manifest, target, ui, artifacts);
        RunPrerequisites(archive.Manifest, target, silent, ui);
        InstallShortcuts(archive.Manifest, target, ui, artifacts, options?.Shortcuts);
        ApplyEnvironmentVariables(archive.Manifest, target, ui);
        if (OperatingSystem.IsWindows()) ApplyRegistryValues(archive.Manifest, target, ui, artifacts);
        RunPostInstall(archive.Manifest, target, silent, ui);
        WriteUninstaller(archive.Manifest, target, ui, artifacts);
        WriteInstallReceipt(archive, target, artifacts);
        return 0;
    }

    private static void ExtractArchive(PackageArchive archive, List<PackageEntry> entries, string target, IInstallerUi ui, List<InstalledArtifact> artifacts)
    {
        string fullTarget = Path.GetFullPath(target);
        Directory.CreateDirectory(fullTarget);
        long total = entries.Sum(e => e.Length);
        long done = 0;
        int index = 0;
        foreach (var entry in entries)
        {
            string outputPath = SafeCombine(fullTarget, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            ReadOnlySpan<byte> content = archive.Raw.AsSpan((int)entry.Offset, (int)entry.Length);
            string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!hash.Equals(entry.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Hash mismatch before extracting {entry.Path}.");
            using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            const int chunkSize = 1024 * 1024;
            long entryDone = 0;
            for (int offset = 0; offset < content.Length; offset += chunkSize)
            {
                int length = Math.Min(chunkSize, content.Length - offset);
                file.Write(content.Slice(offset, length));
                entryDone += length;
                done += length;
                ui.Progress(index + 1, entries.Count, done, total, entry.Path, entryDone, entry.Length);
            }
            if (entry.Length == 0)
                ui.Progress(index + 1, entries.Count, done, total, entry.Path, 0, 0);
            index++;
            artifacts.Add(new InstalledArtifact("file", outputPath));
        }
        ui.ProgressClear();
    }

    internal static string SafeCombine(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Unsafe package path: {relativePath}");

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        string relative = Path.GetRelativePath(fullRoot, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Unsafe package path: {relativePath}");
        return path;
    }

    private static void WriteInstallReceipt(PackageArchive archive, string target, List<InstalledArtifact> artifacts)
    {
        string meta = Path.Combine(target, ".amsetup");
        Directory.CreateDirectory(meta);
        var receiptManifest = archive.Manifest with
        {
            Branding = archive.Manifest.Branding with { SplashImageBase64 = "" }
        };
        var info = new PackageInfo(
            receiptManifest,
            archive.Compression,
            archive.Entries.Select(e => new PackageFile(e.Path, e.Length, e.Sha256, e.ComponentId)).ToList(),
            archive.Entries.Sum(e => e.Length),
            archive.CompressedBytes,
            artifacts);
        string json = JsonSerializer.Serialize(info, AmSetupJsonContext.Default.PackageInfo);
        File.WriteAllText(Path.Combine(meta, "install.json"), json + Environment.NewLine, Encoding.UTF8);
    }

    private static void CreateInstallDirectories(SetupManifest manifest, string target, IInstallerUi ui, List<InstalledArtifact> artifacts)
    {
        string fullTarget = Path.GetFullPath(target);
        foreach (var directory in manifest.InstallDirectories ?? [])
        {
            if (!PlatformDefaults.Matches(directory.OS) || string.IsNullOrWhiteSpace(directory.Path)) continue;
            string expanded = PathTemplate.Expand(directory.Path, manifest, target);
            string output = expanded.Contains("{InstallDir}", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(expanded)
                ? SafeCombineOrRoot(fullTarget, expanded)
                : SafeCombine(fullTarget, expanded);
            Directory.CreateDirectory(output);
            artifacts.Add(new InstalledArtifact("directory", output));
            ui.Line($"Folder: {output}");
        }
    }

    private static string SafeCombineOrRoot(string root, string path)
    {
        if (!Path.IsPathRooted(path)) return SafeCombine(root, path);
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Unsafe install directory path: {path}");
        return fullPath;
    }

    private static void RunPostInstall(SetupManifest manifest, string target, bool silent, IInstallerUi ui)
    {
        foreach (var action in manifest.PostInstall ?? [])
        {
            if (!PlatformDefaults.Matches(action.OS)) continue;
            string command = PathTemplate.Expand(action.Command, manifest, target);
            string arguments = PathTemplate.Expand(action.Arguments, manifest, target);
            if (string.IsNullOrWhiteSpace(command)) continue;

            var start = new ProcessStartInfo(command, arguments) { UseShellExecute = false };
            using var process = Process.Start(start);
            process?.WaitForExit();
            if (process is not null && process.ExitCode != 0 && !action.IgnoreFailure)
                throw new InvalidOperationException($"Post-install action failed: {command} {arguments}");
            if (!silent) ui.Line($"Post-install: {command} {arguments}");
        }
    }

    private static void RunPrerequisites(SetupManifest manifest, string target, bool silent, IInstallerUi ui)
    {
        foreach (var prerequisite in (manifest.Prerequisites ?? []).Where(p => p.Required))
        {
            if (PrerequisiteDetector.IsSatisfied(prerequisite))
            {
                ui.Line($"Prerequisite OK: {prerequisite.Name}");
                continue;
            }

            string message = string.IsNullOrWhiteSpace(prerequisite.Message)
                ? $"{prerequisite.Name} is required."
                : PathTemplate.Expand(prerequisite.Message, manifest, target);
            if (!silent) ui.Line("Prerequisite: " + message);

            string command = ResolvePrerequisiteCommand(prerequisite, manifest, target);
            if (string.IsNullOrWhiteSpace(command))
            {
                if (prerequisite.Required)
                    throw new InvalidOperationException($"Missing required prerequisite: {message}");
                continue;
            }

            string arguments = PathTemplate.Expand(prerequisite.InstallArguments, manifest, target);
            using var process = Process.Start(new ProcessStartInfo(command, arguments) { UseShellExecute = true });
            process?.WaitForExit();
            if (process is not null && process.ExitCode != 0)
                throw new InvalidOperationException($"Prerequisite installer failed: {prerequisite.Name}");
        }
    }

    private static string ResolvePrerequisiteCommand(SetupPrerequisite prerequisite, SetupManifest manifest, string target)
    {
        if (!string.IsNullOrWhiteSpace(prerequisite.InstallCommand))
            return PathTemplate.Expand(prerequisite.InstallCommand, manifest, target);
        if (string.IsNullOrWhiteSpace(prerequisite.BundledPath)) return "";

        string expanded = PathTemplate.Expand(prerequisite.BundledPath, manifest, target);
        return Path.IsPathRooted(expanded) ? expanded : Path.Combine(target, expanded);
    }

    internal static List<SetupShortcut> ShortcutChoices(PackageArchive archive)
    {
        var existing = (archive.Manifest.Shortcuts ?? [])
            .Where(s => PlatformDefaults.Matches(s.OS) && !string.IsNullOrWhiteSpace(s.Target))
            .ToList();
        var primary = existing.FirstOrDefault() ?? DetectPrimaryShortcut(archive);
        if (primary is null) return existing;

        var choices = new List<SetupShortcut>();
        AddChoice("desktop");
        if (OperatingSystem.IsWindows()) AddChoice("startMenu");
        else if (!OperatingSystem.IsMacOS()) AddChoice("applications");
        AddChoice("install");
        return choices;

        void AddChoice(string location)
        {
            var shortcut = existing.FirstOrDefault(s => s.Location.Equals(location, StringComparison.OrdinalIgnoreCase)) ??
                primary with { Location = location };
            if (!choices.Any(s => s.Location.Equals(shortcut.Location, StringComparison.OrdinalIgnoreCase)))
                choices.Add(shortcut);
        }
    }

    private static SetupShortcut? DetectPrimaryShortcut(PackageArchive archive)
    {
        var candidate = archive.Entries.FirstOrDefault(e =>
            e.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            e.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            e.Path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
            e.Path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase));
        if (candidate is null) return null;

        return new SetupShortcut
        {
            OS = "any",
            Name = archive.Manifest.ProductName,
            Target = "{InstallDir}" + Path.DirectorySeparatorChar + candidate.Path.Replace('/', Path.DirectorySeparatorChar),
            Location = OperatingSystem.IsWindows() ? "startMenu" : "applications",
            WorkingDirectory = "{InstallDir}"
        };
    }

    private static void InstallShortcuts(SetupManifest manifest, string target, IInstallerUi ui, List<InstalledArtifact> artifacts, List<SetupShortcut>? selectedShortcuts)
    {
        var shortcuts = selectedShortcuts ?? manifest.Shortcuts ?? [];
        foreach (var shortcut in shortcuts)
        {
            if (!PlatformDefaults.Matches(shortcut.OS)) continue;
            string name = string.IsNullOrWhiteSpace(shortcut.Name) ? manifest.ProductName : shortcut.Name;
            string command = PathTemplate.Expand(shortcut.Target, manifest, target);
            string args = PathTemplate.Expand(shortcut.Arguments, manifest, target);
            string workingDirectory = PathTemplate.Expand(shortcut.WorkingDirectory, manifest, target);
            if (string.IsNullOrWhiteSpace(command)) continue;

            string location = shortcut.Location.Equals("install", StringComparison.OrdinalIgnoreCase)
                ? target
                : PlatformDefaults.ShortcutDirectory(shortcut.Location);
            Directory.CreateDirectory(location);
            if (OperatingSystem.IsWindows())
            {
                string path = Path.Combine(location, PathTemplate.SanitizeSegment(name) + ".cmd");
                File.WriteAllText(path, $"@echo off\r\ncd /d \"{workingDirectory}\"\r\n\"{command}\" {args}\r\n", Encoding.UTF8);
                artifacts.Add(new InstalledArtifact("shortcut", path));
                ui.Line($"Shortcut: {path}");
            }
            else
            {
                string path = Path.Combine(location, PathTemplate.SanitizeSegment(name) + ".desktop");
                File.WriteAllText(path, $"""
                    [Desktop Entry]
                    Type=Application
                    Name={name}
                    Exec="{command}" {args}
                    Path={workingDirectory}
                    Terminal=false
                    Categories=Utility;
                    """, Encoding.UTF8);
                TrySetExecutable(path);
                artifacts.Add(new InstalledArtifact("shortcut", path));
                ui.Line($"Shortcut: {path}");
            }
        }
    }

    private static void ApplyEnvironmentVariables(SetupManifest manifest, string target, IInstallerUi ui)
    {
        foreach (var variable in manifest.EnvironmentVariables ?? [])
        {
            if (!PlatformDefaults.Matches(variable.OS) || string.IsNullOrWhiteSpace(variable.Name)) continue;
            string value = PathTemplate.Expand(variable.Value, manifest, target);
            Environment.SetEnvironmentVariable(variable.Name, value, EnvironmentVariableTarget.Process);

            if (variable.Target.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                if (OperatingSystem.IsWindows())
                {
                    Environment.SetEnvironmentVariable(variable.Name, value, EnvironmentVariableTarget.User);
                }
                else
                {
                    string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".profile");
                    File.AppendAllText(profile, $"{Environment.NewLine}export {variable.Name}=\"{value.Replace("\"", "\\\"")}\"{Environment.NewLine}", Encoding.UTF8);
                }
            }
            ui.Line($"Environment: {variable.Name}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyRegistryValues(SetupManifest manifest, string target, IInstallerUi ui, List<InstalledArtifact> artifacts)
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var value in manifest.RegistryValues ?? [])
        {
            if (!PlatformDefaults.Matches(value.OS) || string.IsNullOrWhiteSpace(value.Key)) continue;
            try
            {
                var root = RegistryRoot(value.Root);
                using var key = root.CreateSubKey(PathTemplate.Expand(value.Key, manifest, target), writable: true)
                    ?? throw new InvalidOperationException("Could not create registry key.");
                object data = RegistryData(PathTemplate.Expand(value.Value, manifest, target), value.ValueKind);
                key.SetValue(value.Name, data, RegistryKind(value.ValueKind));
                artifacts.Add(new InstalledArtifact("registry", "", value.Root, PathTemplate.Expand(value.Key, manifest, target), value.Name));
                ui.Line($"Registry: {value.Root}\\{value.Key}");
            }
            catch when (value.IgnoreFailure)
            {
                ui.Line($"Registry skipped: {value.Root}\\{value.Key}");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static Microsoft.Win32.RegistryKey RegistryRoot(string root) => root.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
        "HKCR" or "HKEY_CLASSES_ROOT" => Microsoft.Win32.Registry.ClassesRoot,
        "HKU" or "HKEY_USERS" => Microsoft.Win32.Registry.Users,
        "HKCC" or "HKEY_CURRENT_CONFIG" => Microsoft.Win32.Registry.CurrentConfig,
        _ => Microsoft.Win32.Registry.CurrentUser
    };

    [SupportedOSPlatform("windows")]
    private static Microsoft.Win32.RegistryValueKind RegistryKind(string kind) => kind.ToLowerInvariant() switch
    {
        "dword" => Microsoft.Win32.RegistryValueKind.DWord,
        "qword" => Microsoft.Win32.RegistryValueKind.QWord,
        "expandstring" => Microsoft.Win32.RegistryValueKind.ExpandString,
        "multistring" => Microsoft.Win32.RegistryValueKind.MultiString,
        "binary" => Microsoft.Win32.RegistryValueKind.Binary,
        _ => Microsoft.Win32.RegistryValueKind.String
    };

    private static object RegistryData(string value, string kind) => kind.ToLowerInvariant() switch
    {
        "dword" => int.Parse(value),
        "qword" => long.Parse(value),
        "multistring" => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        "binary" => Convert.FromHexString(value.Replace(" ", "", StringComparison.Ordinal)),
        _ => value
    };

    private static void TrySetExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("chmod", $"+x \"{path}\"") { UseShellExecute = false });
            process?.WaitForExit();
        }
        catch { }
    }

    private static void WriteUninstaller(SetupManifest manifest, string target, IInstallerUi ui, List<InstalledArtifact> artifacts)
    {
        string meta = Path.Combine(target, ".amsetup");
        Directory.CreateDirectory(meta);
        string uninstallerName = OperatingSystem.IsWindows() ? "uninstall.exe" : "uninstall";
        string uninstallerPath = Path.Combine(meta, uninstallerName);
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            try
            {
                File.Copy(processPath, uninstallerPath, overwrite: true);
                CopyUninstallerSidecars(processPath, meta);
                if (!OperatingSystem.IsWindows()) TrySetExecutable(uninstallerPath);
                artifacts.Add(new InstalledArtifact("uninstaller", uninstallerPath));
            }
            catch
            {
                // Some development apphost layouts cannot be copied as standalone uninstallers.
                // The receipt and uninstall scripts still allow command-line uninstalling.
            }
        }

        string displayName = PathTemplate.SanitizeSegment("Uninstall " + manifest.ProductName);
        if (OperatingSystem.IsWindows())
        {
            string rootScript = Path.Combine(target, displayName + ".cmd");
            string command = File.Exists(uninstallerPath) ? uninstallerPath : processPath ?? "amSetup.exe";
            File.WriteAllText(rootScript, $"@echo off\r\n\"{command}\" uninstall --target \"{target}\"\r\n", Encoding.UTF8);
            artifacts.Add(new InstalledArtifact("uninstaller", rootScript));
            ui.Line($"Uninstaller: {rootScript}");
        }
        else
        {
            string rootScript = Path.Combine(target, displayName + ".sh");
            string command = File.Exists(uninstallerPath) ? uninstallerPath : processPath ?? "amSetup";
            File.WriteAllText(rootScript, $"#!/usr/bin/env sh\n\"{command}\" uninstall --target \"{target}\"\n", Encoding.UTF8);
            TrySetExecutable(rootScript);
            artifacts.Add(new InstalledArtifact("uninstaller", rootScript));
            ui.Line($"Uninstaller: {rootScript}");
        }
    }

    private static void CopyUninstallerSidecars(string processPath, string meta)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(processPath)) ?? ".";
        foreach (string sidecar in Directory.EnumerateFiles(directory, "amSetup.*"))
        {
            string name = Path.GetFileName(sidecar);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(sidecar, Path.Combine(meta, name), overwrite: true);
        }
    }

    private static string? ValueAfter(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

internal static class Inspector
{
    public static int Run(AttachedPackage attached)
    {
        var archive = PackageStore.ReadPackage(attached);
        Console.WriteLine($"{archive.Manifest.ProductName} {archive.Manifest.Version}");
        Console.WriteLine($"Identifier: {archive.Manifest.Identifier}");
        Console.WriteLine($"Publisher:  {archive.Manifest.Publisher}");
        Console.WriteLine($"Files:      {archive.Entries.Count}");
        Console.WriteLine($"Package:    {archive.CompressedBytes:N0} bytes");
        Console.WriteLine($"Compression:{archive.Compression}");
        return 0;
    }
}

internal static class Uninstaller
{
    public static int Run(string[] args)
    {
        bool silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        string target = ValueAfter(args, "--target") ?? ResolveTargetFromProcess();
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Missing --target <dir>.");

        string fullTarget = Path.GetFullPath(target);
        string receiptPath = Path.Combine(fullTarget, ".amsetup", "install.json");
        if (!File.Exists(receiptPath)) throw new FileNotFoundException("Install receipt was not found.", receiptPath);

        var receipt = JsonSerializer.Deserialize(File.ReadAllText(receiptPath, Encoding.UTF8), AmSetupJsonContext.Default.PackageInfo)
            ?? throw new InvalidDataException("Install receipt could not be read.");
        var ui = new ConsoleInstallerUi(receipt.Manifest.Theme);
        ui.Header($"Uninstall {receipt.Manifest.ProductName}");

        var artifacts = receipt.Artifacts ?? [];
        var fileArtifacts = artifacts
            .Where(a => a.Kind.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                        a.Kind.Equals("shortcut", StringComparison.OrdinalIgnoreCase) ||
                        a.Kind.Equals("uninstaller", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (fileArtifacts.Count == 0)
            fileArtifacts = receipt.Files.Select(f => Installer.SafeCombine(fullTarget, f.Path)).ToList();

        string? currentProcess = Environment.ProcessPath is { Length: > 0 } p ? Path.GetFullPath(p) : null;
        int total = Math.Max(fileArtifacts.Count, 1);
        int index = 0;
        foreach (string path in fileArtifacts.OrderByDescending(p => p.Length))
        {
            string fullPath = RequireInsideTarget(fullTarget, path);
            index++;
            if (!dryRun && File.Exists(fullPath) && !IsSamePath(fullPath, currentProcess))
                File.Delete(fullPath);
            ui.Progress(index, total, index, total, fullPath, 1, 1);
        }
        ui.ProgressClear();

        foreach (var artifact in artifacts.Where(a => a.Kind.Equals("registry", StringComparison.OrdinalIgnoreCase)))
            RemoveRegistryValue(artifact, ui, dryRun);

        var directories = artifacts
            .Where(a => a.Kind.Equals("directory", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Path)
            .Concat(Directory.Exists(fullTarget) ? Directory.EnumerateDirectories(fullTarget, "*", SearchOption.AllDirectories) : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length);
        foreach (string directory in directories)
        {
            string fullDirectory = RequireInsideTarget(fullTarget, directory);
            if (!dryRun && Directory.Exists(fullDirectory) && !Directory.EnumerateFileSystemEntries(fullDirectory).Any())
                Directory.Delete(fullDirectory);
        }

        if (!dryRun)
        {
            TryDelete(receiptPath);
            TryDeleteEmpty(Path.Combine(fullTarget, ".amsetup"));
            TryDeleteEmpty(fullTarget);
            ScheduleSelfCleanup(fullTarget, currentProcess);
        }

        if (!silent) ui.Line(dryRun ? "Dry-run uninstall complete." : "Uninstall complete.");
        return 0;
    }

    private static string? ValueAfter(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string ResolveTargetFromProcess()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)) return "";
        string directory = Path.GetDirectoryName(Path.GetFullPath(processPath)) ?? "";
        if (Path.GetFileName(directory).Equals(".amsetup", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(directory) ?? "";
        return directory;
    }

    private static string RequireInsideTarget(string target, string path)
    {
        string fullTarget = Path.GetFullPath(target);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullTarget, fullPath);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Refusing to uninstall outside target: {path}");
        return fullPath;
    }

    private static bool IsSamePath(string left, string? right) =>
        !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RemoveRegistryValue(InstalledArtifact artifact, IInstallerUi ui, bool dryRun)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!dryRun)
            {
                var root = RegistryRoot(artifact.Root);
                using var key = root.OpenSubKey(artifact.Key, writable: true);
                key?.DeleteValue(artifact.Name, throwOnMissingValue: false);
            }
            ui.Line($"Registry removed: {artifact.Root}\\{artifact.Key}");
        }
        catch
        {
            ui.Line($"Registry removal skipped: {artifact.Root}\\{artifact.Key}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static Microsoft.Win32.RegistryKey RegistryRoot(string root) => root.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
        "HKCR" or "HKEY_CLASSES_ROOT" => Microsoft.Win32.Registry.ClassesRoot,
        "HKU" or "HKEY_USERS" => Microsoft.Win32.Registry.Users,
        "HKCC" or "HKEY_CURRENT_CONFIG" => Microsoft.Win32.Registry.CurrentConfig,
        _ => Microsoft.Win32.Registry.CurrentUser
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch { }
    }

    private static void ScheduleSelfCleanup(string target, string? currentProcess)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(currentProcess)) return;
        string meta = Path.Combine(Path.GetFullPath(target), ".amsetup");
        if (!Path.GetFullPath(currentProcess).StartsWith(Path.GetFullPath(meta), StringComparison.OrdinalIgnoreCase)) return;

        string script = Path.Combine(Path.GetTempPath(), "amsetup-uninstall-cleanup-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(script, $"""
            @echo off
            timeout /t 2 /nobreak >nul
            rmdir /s /q "{meta}" 2>nul
            rmdir "{Path.GetFullPath(target)}" 2>nul
            del /f /q "%~f0" 2>nul
            """, Encoding.UTF8);
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
    }
}

internal static class PlatformDefaults
{
    public static string InstallDirectoryTemplate(string publisher, string identifier, string productName)
    {
        if (OperatingSystem.IsWindows()) return "{ProgramFiles}\\{Publisher}\\{ProductName}";
        if (OperatingSystem.IsMacOS()) return "{Home}/Applications/{ProductName}";
        return "{Home}/.local/share/{Identifier}";
    }

    public static bool Matches(string os) =>
        os.Equals("any", StringComparison.OrdinalIgnoreCase) ||
        (OperatingSystem.IsWindows() && os.Equals("windows", StringComparison.OrdinalIgnoreCase)) ||
        (OperatingSystem.IsLinux() && os.Equals("linux", StringComparison.OrdinalIgnoreCase)) ||
        (OperatingSystem.IsMacOS() && os.Equals("macos", StringComparison.OrdinalIgnoreCase));

    public static string ShortcutDirectory(string location)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return location.Equals("startMenu", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Applications");

        return location.Equals("applications", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(home, ".local", "share", "applications")
            : Path.Combine(home, "Desktop");
    }
}

internal static class PathTemplate
{
    public static string Expand(string template, SetupManifest manifest, string? target = null)
    {
        string result = template
            .Replace("{ProductName}", SanitizeSegment(manifest.ProductName), StringComparison.OrdinalIgnoreCase)
            .Replace("{Identifier}", SanitizeSegment(manifest.Identifier), StringComparison.OrdinalIgnoreCase)
            .Replace("{Publisher}", SanitizeSegment(manifest.Publisher), StringComparison.OrdinalIgnoreCase)
            .Replace("{Version}", manifest.Version, StringComparison.OrdinalIgnoreCase)
            .Replace("{Home}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            .Replace("{LocalAppData}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("{AppData}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("{ProgramFiles}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);
        if (target is not null) result = result.Replace("{InstallDir}", target, StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(result);
    }

    public static string SanitizeSegment(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '-');
        return value.Trim();
    }
}

internal static class LicenseTextProcessor
{
    public static string? PrepareForPackage(SetupManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.LicenseText)) return manifest.LicenseText;

        string text = NormalizeNewlines(manifest.LicenseText);
        text = ExpandTokens(text, manifest);
        text = FillCommonLicensePlaceholders(text, manifest);
        return WrapParagraphs(text, 96);
    }

    private static string ExpandTokens(string text, SetupManifest manifest)
    {
        string year = DateTime.UtcNow.Year.ToString();
        return text
            .Replace("{ProductName}", manifest.ProductName, StringComparison.OrdinalIgnoreCase)
            .Replace("{Identifier}", manifest.Identifier, StringComparison.OrdinalIgnoreCase)
            .Replace("{Version}", manifest.Version, StringComparison.OrdinalIgnoreCase)
            .Replace("{Publisher}", manifest.Publisher, StringComparison.OrdinalIgnoreCase)
            .Replace("{Company}", manifest.Publisher, StringComparison.OrdinalIgnoreCase)
            .Replace("{CopyrightYear}", year, StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", year, StringComparison.OrdinalIgnoreCase);
    }

    private static string FillCommonLicensePlaceholders(string text, SetupManifest manifest)
    {
        string product = string.IsNullOrWhiteSpace(manifest.ProductName) ? "Application" : manifest.ProductName.Trim();
        string publisher = string.IsNullOrWhiteSpace(manifest.Publisher) ? "Your Company" : manifest.Publisher.Trim();
        string description = string.IsNullOrWhiteSpace(manifest.Description)
            ? product
            : manifest.Description.Trim().TrimEnd('.');
        string year = DateTime.UtcNow.Year.ToString();

        return text
            .Replace("<program>", product, StringComparison.OrdinalIgnoreCase)
            .Replace("<year>", year, StringComparison.OrdinalIgnoreCase)
            .Replace("<name of author>", publisher, StringComparison.OrdinalIgnoreCase)
            .Replace("<one line to give the program's name and a brief idea of what it does.>", description + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static string WrapParagraphs(string text, int width)
    {
        var blocks = NormalizeNewlines(text)
            .Split(["\n\n"], StringSplitOptions.None)
            .Select(block => WrapBlock(block, width));
        return string.Join(Environment.NewLine + Environment.NewLine, blocks).TrimEnd();
    }

    private static string WrapBlock(string block, int width)
    {
        string[] lines = block.Split('\n');
        if (lines.Length <= 1 || lines.Any(l => l.StartsWith("    ", StringComparison.Ordinal)))
            return string.Join(Environment.NewLine, lines.Select(l => l.TrimEnd()));

        string joined = string.Join(" ", lines.Select(l => l.Trim()).Where(l => l.Length > 0));
        if (joined.Length <= width) return joined;

        var output = new List<string>();
        var current = new StringBuilder();
        foreach (string word in joined.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                output.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }

        if (current.Length > 0) output.Add(current.ToString());
        return string.Join(Environment.NewLine, output);
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

internal static class ComponentMatcher
{
    public static string ComponentFor(string relativePath, SetupManifest manifest)
    {
        string normalized = relativePath.Replace('\\', '/');
        foreach (var component in manifest.Components ?? [])
        {
            if (component.Include.Count == 0) continue;
            foreach (string pattern in component.Include)
            {
                if (MatchesPattern(normalized, pattern.Replace('\\', '/')))
                    return component.Id;
            }
        }
        return manifest.Components?.FirstOrDefault(c => c.Required)?.Id ?? manifest.Components?.FirstOrDefault()?.Id ?? "main";
    }

    private static bool MatchesPattern(string path, string pattern)
    {
        if (pattern == "**" || pattern == "*") return true;
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
            return path.StartsWith(pattern[..^3].TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith("*", StringComparison.Ordinal))
            return path.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return path.Equals(pattern, StringComparison.OrdinalIgnoreCase) || path.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }
}

internal interface IInstallerUi
{
    void Header(string text);
    void Line(string text);
    void Progress(int index, int count, long bytes, long total, string current, long currentBytes = 0, long currentTotal = 0);
    void ProgressClear();
}

internal sealed class ConsoleInstallerUi : IInstallerUi
{
    private readonly SetupTheme _theme;

    public ConsoleInstallerUi(SetupTheme theme) => _theme = theme;

    public void Header(string text)
    {
        Console.ForegroundColor = Color(_theme.HeaderColor, ConsoleColor.White);
        Console.WriteLine(_theme.Banner.Equals("minimal", StringComparison.OrdinalIgnoreCase) ? text : $"==== {text} ====");
        Console.ResetColor();
    }

    public void Line(string text)
    {
        Console.ForegroundColor = Color(_theme.AccentColor, ConsoleColor.Cyan);
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public void Progress(int index, int count, long bytes, long total, string current, long currentBytes = 0, long currentTotal = 0)
    {
        int percent = total <= 0 ? 100 : (int)Math.Clamp(bytes * 100 / total, 0, 100);
        int width = 28;
        int filled = percent * width / 100;
        Console.ForegroundColor = Color(_theme.ProgressColor, ConsoleColor.Green);
        int filePercent = currentTotal <= 0 ? 100 : (int)Math.Clamp(currentBytes * 100 / currentTotal, 0, 100);
        Console.Write($"\r[{new string('#', filled).PadRight(width, '.')}] {percent,3}% file {filePercent,3}% {index}/{count} {Trim(current, 36)}   ");
        Console.ResetColor();
    }

    public void ProgressClear() => Console.WriteLine();

    private static string Trim(string value, int max) => value.Length <= max ? value : "..." + value[^Math.Max(0, max - 3)..];

    private static ConsoleColor Color(string name, ConsoleColor fallback) =>
        Enum.TryParse<ConsoleColor>(name, ignoreCase: true, out var color) ? color : fallback;
}

internal static class SizeParser
{
    public static long Parse(string value)
    {
        value = value.Trim();
        long multiplier = 1;
        char suffix = char.ToLowerInvariant(value[^1]);
        if (suffix is 'k' or 'm' or 'g')
        {
            value = value[..^1];
            multiplier = suffix switch
            {
                'k' => 1024L,
                'm' => 1024L * 1024L,
                'g' => 1024L * 1024L * 1024L,
                _ => 1
            };
        }
        return long.Parse(value) * multiplier;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SetupManifest))]
[JsonSerializable(typeof(SetupAction))]
[JsonSerializable(typeof(SetupBranding))]
[JsonSerializable(typeof(SetupTheme))]
[JsonSerializable(typeof(SetupComponent))]
[JsonSerializable(typeof(SetupInstallDirectory))]
[JsonSerializable(typeof(SetupShortcut))]
[JsonSerializable(typeof(SetupEnvironmentVariable))]
[JsonSerializable(typeof(SetupRegistryValue))]
[JsonSerializable(typeof(SetupPrerequisite))]
[JsonSerializable(typeof(SetupWindow))]
[JsonSerializable(typeof(PackageInfo))]
[JsonSerializable(typeof(PackageFile))]
[JsonSerializable(typeof(InstalledArtifact))]
[JsonSerializable(typeof(SetupBuilderProject))]
[JsonSerializable(typeof(BuilderState))]
[JsonSerializable(typeof(BuilderSaveRequest))]
[JsonSerializable(typeof(BuilderAnalyzeRequest))]
[JsonSerializable(typeof(BuilderBuildRequest))]
[JsonSerializable(typeof(BuilderBuildResult))]
[JsonSerializable(typeof(BrowseEntry))]
[JsonSerializable(typeof(BrowseResult))]
[JsonSerializable(typeof(PayloadFileResult))]
[JsonSerializable(typeof(DependencyReport))]
[JsonSerializable(typeof(FileInventoryItem))]
[JsonSerializable(typeof(DotNetApplicationInfo))]
[JsonSerializable(typeof(DependencyIssue))]
internal sealed partial class AmSetupJsonContext : JsonSerializerContext;
