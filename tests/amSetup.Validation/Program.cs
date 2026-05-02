// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AmSetup.Validation;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            string tool = args.Length > 0
                ? args[0]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "amSetup", "bin", "Release", "net10.0", OperatingSystem.IsWindows() ? "amSetup.exe" : "amSetup"));
            if (!File.Exists(tool)) throw new FileNotFoundException("amSetup executable was not found. Build Release first or pass path.", tool);

            string root = Path.Combine(Path.GetTempPath(), "amsetup-validation-" + Guid.NewGuid().ToString("N"));
            string payload = Path.Combine(root, "payload");
            string install = Path.Combine(root, "install");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "app.txt"), "hello from amSetup" + Environment.NewLine, Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(payload, "bin"));
            File.WriteAllText(Path.Combine(payload, "bin", "run.sh"), "echo hi" + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(Path.Combine(payload, "ValidationApp.runtimeconfig.json"), """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "10.0.0"
                    }
                  }
                }
                """, Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(payload, "docs"));
            File.WriteAllText(Path.Combine(payload, "docs", "readme.txt"), "optional docs" + Environment.NewLine, Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(root, "splash.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));

            string manifest = Path.Combine(root, "amsetup.json");
            File.WriteAllText(manifest, """
                {
                  "productName": "Validation App",
                  "identifier": "validation-app",
                  "version": "1.2.3",
                  "publisher": "amSetup",
                  "description": "Validation package",
                  "defaultInstallDirectory": "{Home}/.amsetup-validation/ValidationApp",
                  "licenseText": "Validation license for {ProductName} {Version} by {Publisher}.\n\n<program> Copyright (C) <year> <name of author>",
                  "requireConfirmation": false,
                  "branding": {
                    "iconPath": "",
                    "splashPath": "splash.png",
                    "showSplash": true,
                    "splashDurationMilliseconds": 250,
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
                    "introText": "This setup will install the validation app.",
                    "footerText": "Validation footer",
                    "style": "classic",
                    "width": 720,
                    "height": 460,
                    "showSidebar": true
                  },
                  "components": [
                    {
                      "id": "main",
                      "name": "Main Files",
                      "description": "Required files",
                      "required": true,
                      "defaultSelected": true,
                      "include": [ "app.txt", "bin/**" ]
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
                      "name": "Validation App",
                      "target": "{InstallDir}/app.txt",
                      "arguments": "",
                      "location": "install",
                      "workingDirectory": "{InstallDir}"
                    }
                  ],
                  "environmentVariables": [
                    {
                      "os": "any",
                      "name": "AMSETUP_VALIDATION_HOME",
                      "value": "{InstallDir}",
                      "target": "process"
                    }
                  ],
                  "registryValues": [
                    {
                      "os": "linux",
                      "root": "HKCU",
                      "key": "Software\\ValidationApp",
                      "name": "InstallDir",
                      "value": "{InstallDir}",
                      "valueKind": "String",
                      "ignoreFailure": true
                    }
                  ],
                  "postInstall": []
                }
                """, Encoding.UTF8);

            string installer = Path.Combine(root, OperatingSystem.IsWindows() ? "validation-installer.exe" : "validation-installer");
            Run(tool, $"pack --manifest \"{manifest}\" --payload \"{payload}\" --output \"{installer}\" --stub \"{tool}\" --compression balanced --layout split --chunk-size 128 --allow-framework-dependent-stub");
            CopyFrameworkSidecars(tool, installer);
            Run(installer, $"install --target \"{install}\" --components main --silent");
            Run(installer, "inspect");
            Run(installer, "install --list");

            string report = Path.Combine(root, "dependency-report.json");
            Run(tool, $"analyze --payload \"{payload}\" --manifest \"{manifest}\" --output \"{report}\"");
            string reportText = File.Exists(report) ? File.ReadAllText(report, Encoding.UTF8) : "";
            if (!reportText.Contains("\"totalFiles\": 4", StringComparison.Ordinal))
                throw new InvalidOperationException("Dependency analyzer did not write the expected report.");
            if (!reportText.Contains("\"kind\": \"dotnet-runtime\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Dependency analyzer did not detect the .NET runtime prerequisite.");

            string project = Path.Combine(root, "amsetup.project.json");
            string projectInstaller = Path.Combine(root, OperatingSystem.IsWindows() ? "project-installer.exe" : "project-installer");
            File.WriteAllText(project, JsonSerializer.Serialize(new
            {
                manifestPath = manifest,
                payloadPath = payload,
                outputPath = projectInstaller,
                stubPath = tool,
                compression = "Balanced",
                layout = "External",
                chunkSize = "128"
            }) + Environment.NewLine, Encoding.UTF8);
            Run(tool, $"build-project --project \"{project}\" --allow-framework-dependent-stub");
            CopyFrameworkSidecars(tool, projectInstaller);
            Run(projectInstaller, $"install --target \"{Path.Combine(root, "project-install")}\" --silent");
            SmokeBuilderUi(tool, project);
            SmokeDoubleClickBuilder(tool, Path.Combine(root, "double-click", "amsetup.project.json"));

            AssertFile(Path.Combine(install, "app.txt"), "hello from amSetup");
            AssertFile(Path.Combine(install, "bin", "run.sh"), "echo hi");
            if (File.Exists(Path.Combine(install, "docs", "readme.txt")))
                throw new InvalidOperationException("Optional docs component should not have been installed.");
            if (!File.Exists(Path.Combine(install, OperatingSystem.IsWindows() ? "Validation App.cmd" : "Validation App.desktop")))
                throw new InvalidOperationException("Shortcut was not created in install directory.");
            if (!Directory.Exists(Path.Combine(install, "data")) || !Directory.Exists(Path.Combine(install, "config")))
                throw new InvalidOperationException("Install directories were not created.");
            if (!File.Exists(Path.Combine(install, ".amsetup", "install.json")))
                throw new InvalidOperationException("Install receipt was not written.");
            string receipt = File.ReadAllText(Path.Combine(install, ".amsetup", "install.json"), Encoding.UTF8);
            if (!receipt.Contains("Validation license for Validation App 1.2.3 by amSetup", StringComparison.Ordinal) ||
                receipt.Contains("<program>", StringComparison.Ordinal) ||
                receipt.Contains("<name of author>", StringComparison.Ordinal))
                throw new InvalidOperationException("License text was not prepared with manifest metadata.");
            if (!File.Exists(Path.Combine(install, ".amsetup", OperatingSystem.IsWindows() ? "uninstall.exe" : "uninstall")) &&
                !File.Exists(Path.Combine(install, $"Uninstall Validation App{(OperatingSystem.IsWindows() ? ".cmd" : ".sh")}")))
                throw new InvalidOperationException("Uninstaller was not created.");

            Run(installer, $"uninstall --target \"{install}\" --silent");
            if (File.Exists(Path.Combine(install, "app.txt")) || File.Exists(Path.Combine(install, ".amsetup", "install.json")))
                throw new InvalidOperationException("Uninstall did not remove installed files and receipt.");

            Console.WriteLine("amSetup validation passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void SmokeBuilderUi(string tool, string project)
    {
        int port = GetFreePort();
        var start = new ProcessStartInfo(tool, $"builder --project \"{project}\" --port {port} --no-browser")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start builder UI.");
        try
        {
            using var client = new HttpClient();
            string baseUrl = $"http://127.0.0.1:{port}";
            string html = RetryGet(client, baseUrl + "/");
            if (!html.Contains("amSetup Builder", StringComparison.Ordinal))
                throw new InvalidOperationException("Builder UI HTML did not load.");
            if (!html.Contains("Preview", StringComparison.Ordinal) ||
                !html.Contains("Window Designer", StringComparison.Ordinal) ||
                !html.Contains("Prerequisites", StringComparison.Ordinal) ||
                !html.Contains("Theme preset", StringComparison.Ordinal) ||
                !html.Contains("Install Folders", StringComparison.Ordinal) ||
                !html.Contains("Registry", StringComparison.Ordinal) ||
                !html.Contains("Browse", StringComparison.Ordinal))
                throw new InvalidOperationException("Builder UI is missing guided preview/theme/folder/browse/registry controls.");

            string state = RetryGet(client, baseUrl + "/api/state?project=" + Uri.EscapeDataString(project));
            if (!state.Contains("\"payloadPath\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Builder UI state endpoint did not return a project.");

            string browse = RetryGet(client, baseUrl + "/api/browse?kind=folder&path=" + Uri.EscapeDataString(Path.GetDirectoryName(project)!));
            if (!browse.Contains("\"directories\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Builder browse endpoint did not return directory data.");

            string payload = Path.GetDirectoryName(project) is { } dir
                ? Path.Combine(dir, "payload")
                : "";
            string files = RetryGet(client, baseUrl + "/api/payload-files?project=" + Uri.EscapeDataString(project) + "&payload=" + Uri.EscapeDataString(payload));
            if (!files.Contains("bin/run.sh", StringComparison.Ordinal))
                throw new InvalidOperationException("Builder payload file endpoint did not return payload executables.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static void SmokeDoubleClickBuilder(string tool, string project)
    {
        int port = GetFreePort();
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["AMSETUP_NO_BROWSER"] = "1";
        start.Environment["AMSETUP_BUILDER_PORT"] = port.ToString();
        start.Environment["AMSETUP_BUILDER_PROJECT"] = project;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start no-argument builder UI.");
        try
        {
            using var client = new HttpClient();
            string html = RetryGet(client, $"http://127.0.0.1:{port}/");
            if (!html.Contains("amSetup Builder", StringComparison.Ordinal))
                throw new InvalidOperationException("No-argument builder UI did not load.");
            if (!File.Exists(project))
                throw new InvalidOperationException("No-argument builder launch did not create a project file.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static string RetryGet(HttpClient client, string url)
    {
        Exception? last = null;
        for (int i = 0; i < 40; i++)
        {
            try
            {
                return client.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(125);
            }
        }

        throw new InvalidOperationException("HTTP smoke request failed: " + url, last);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void CopyFrameworkSidecars(string tool, string installer)
    {
        string sourceDir = Path.GetDirectoryName(Path.GetFullPath(tool))!;
        string targetDir = Path.GetDirectoryName(Path.GetFullPath(installer))!;
        foreach (string file in Directory.EnumerateFiles(sourceDir, "amSetup.*"))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(targetDir, name), overwrite: true);
        }
    }

    private static void Run(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + file);
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Console.Write(stdout);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {arguments} failed with {process.ExitCode}: {stderr}");
    }

    private static void AssertFile(string path, string expectedPrefix)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        string text = File.ReadAllText(path, Encoding.UTF8);
        if (!text.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected content in {path}");
    }
}
