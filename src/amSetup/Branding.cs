using System.Runtime.InteropServices;
using System.Reflection;

namespace AmSetup;

internal static class BrandingProcessor
{
    public static SetupManifest PrepareForPackage(SetupManifest manifest, string baseDirectory)
    {
        var branding = manifest.Branding;
        string icon = ResolveOptionalFile(baseDirectory, branding.IconPath) ?? branding.IconPath;
        string splashPath = ResolveOptionalFile(baseDirectory, branding.SplashPath) ?? "";

        if (branding.ShowSplash && !string.IsNullOrWhiteSpace(splashPath))
        {
            byte[] bytes = File.ReadAllBytes(splashPath);
            if (bytes.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Splash image is too large. Keep it under 4 MB.");

            branding = branding with
            {
                IconPath = icon,
                SplashPath = Path.GetFileName(splashPath),
                SplashImageBase64 = Convert.ToBase64String(bytes),
                SplashContentType = ContentType(splashPath)
            };
        }
        else
        {
            branding = branding with { IconPath = icon, SplashImageBase64 = "", SplashContentType = "" };
        }

        return manifest with { Branding = branding };
    }

    private static string? ResolveOptionalFile(string baseDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(baseDirectory, path));
        return File.Exists(full) ? full : null;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/png"
    };
}

internal static class BuiltInAssets
{
    public static string DefaultIconPath() => Materialize("amsetup.ico", "assets.amsetup.ico");

    public static string DefaultSplashPath() => Materialize("amsetup-oldschool-icon-256.png", "assets.amsetup-oldschool-icon-256.png");

    private static string Materialize(string fileName, string resourceSuffix)
    {
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", fileName));
        if (File.Exists(source)) return source;

        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "amSetup", "assets");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, fileName);
        if (File.Exists(output)) return output;

        var assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return output;

        using Stream input = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidDataException("Embedded asset could not be opened.");
        using var file = File.Create(output);
        input.CopyTo(file);
        return output;
    }
}

internal static class InstallerSplash
{
    public static void Show(SetupManifest manifest)
    {
        var branding = manifest.Branding;
        if (!branding.ShowSplash || string.IsNullOrWhiteSpace(branding.SplashImageBase64)) return;

        string extension = branding.SplashContentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
        string path = Path.Combine(Path.GetTempPath(), "amsetup-splash-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.WriteAllBytes(path, Convert.FromBase64String(branding.SplashImageBase64));
            Console.WriteLine();
            Console.WriteLine($"Launching {manifest.ProductName} setup...");
            Console.WriteLine($"Splash: {path}");

            Open(path);
            int delay = Math.Clamp(branding.SplashDurationMilliseconds, 250, 10000);
            Thread.Sleep(delay);
        }
        catch
        {
            Console.WriteLine($"Launching {manifest.ProductName} setup...");
            Thread.Sleep(Math.Clamp(branding.SplashDurationMilliseconds, 250, 3000));
        }
    }

    private static void Open(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", path);
            else
                System.Diagnostics.Process.Start("xdg-open", path);
        }
        catch
        {
        }
    }
}

internal static class WindowsIconUpdater
{
    private const int RtIcon = 3;
    private const int RtGroupIcon = 14;
    private static readonly IntPtr UpdateFailed = new(-1);

    public static void TryApply(string executablePath, string iconPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) return;
        if (!Path.GetExtension(iconPath).Equals(".ico", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            Apply(executablePath, iconPath);
        }
        catch
        {
            // Icon stamping is a packaging nicety. Do not fail an otherwise valid installer.
        }
    }

    private static void Apply(string executablePath, string iconPath)
    {
        var icon = IconFile.Read(iconPath);
        IntPtr handle = BeginUpdateResource(executablePath, false);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not open executable resources.");

        bool discard = true;
        try
        {
            for (int i = 0; i < icon.Images.Count; i++)
            {
                ushort id = (ushort)(i + 1);
                if (!UpdateResource(handle, (IntPtr)RtIcon, (IntPtr)id, 0, icon.Images[i].Data, icon.Images[i].Data.Length))
                    throw new InvalidOperationException("Could not update icon image resource.");
            }

            byte[] group = icon.CreateGroupResource();
            if (!UpdateResource(handle, (IntPtr)RtGroupIcon, (IntPtr)1, 0, group, group.Length))
                throw new InvalidOperationException("Could not update icon group resource.");
            discard = false;
        }
        finally
        {
            EndUpdateResource(handle, discard);
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, int cbData);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

    private sealed record IconImage(byte Width, byte Height, byte ColorCount, byte Reserved, ushort Planes, ushort BitCount, byte[] Data);

    private sealed class IconFile
    {
        public List<IconImage> Images { get; } = [];

        public static IconFile Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var reader = new BinaryReader(new MemoryStream(bytes));
            if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
                throw new InvalidDataException("Not an ICO file.");

            ushort count = reader.ReadUInt16();
            var entries = new (byte Width, byte Height, byte ColorCount, byte Reserved, ushort Planes, ushort BitCount, uint Size, uint Offset)[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = (reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadUInt32());
            }

            var icon = new IconFile();
            foreach (var entry in entries)
            {
                byte[] data = bytes.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
                icon.Images.Add(new IconImage(entry.Width, entry.Height, entry.ColorCount, entry.Reserved, entry.Planes, entry.BitCount, data));
            }

            return icon;
        }

        public byte[] CreateGroupResource()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)Images.Count);
            for (int i = 0; i < Images.Count; i++)
            {
                var image = Images[i];
                writer.Write(image.Width);
                writer.Write(image.Height);
                writer.Write(image.ColorCount);
                writer.Write(image.Reserved);
                writer.Write(image.Planes);
                writer.Write(image.BitCount);
                writer.Write((uint)image.Data.Length);
                writer.Write((ushort)(i + 1));
            }

            return stream.ToArray();
        }
    }
}
