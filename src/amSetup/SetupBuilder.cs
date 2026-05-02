// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AmSetup;

internal sealed record SetupBuilderProject
{
    public string ManifestPath { get; init; } = "amsetup.json";
    public string PayloadPath { get; init; } = "payload";
    public string OutputPath { get; init; } = "dist/Setup.exe";
    public string StubPath { get; init; } = "";
    public CompressionModeName Compression { get; init; } = CompressionModeName.Balanced;
    public PackageLayout Layout { get; init; } = PackageLayout.Embedded;
    public string ChunkSize { get; init; } = "512m";
}

internal sealed record BuilderState(string ProjectFile, SetupBuilderProject Project, SetupManifest Manifest, string DefaultIconPath, string DefaultSplashPath);
internal sealed record BuilderSaveRequest(string ProjectFile, SetupBuilderProject Project, SetupManifest Manifest);
internal sealed record BuilderAnalyzeRequest(string ProjectFile, SetupBuilderProject Project, SetupManifest Manifest);
internal sealed record BuilderBuildRequest(string ProjectFile, SetupBuilderProject Project, SetupManifest Manifest, bool AllowFrameworkDependentStub);
internal sealed record BuilderBuildResult(bool Success, string Message, string OutputPath);
internal sealed record BrowseEntry(string Name, string Path, bool IsDirectory);
internal sealed record BrowseResult(string Path, string ParentPath, List<BrowseEntry> Drives, List<BrowseEntry> Directories, List<BrowseEntry> Files);
internal sealed record PayloadFileResult(List<BrowseEntry> Executables, List<BrowseEntry> Files);

internal static class BuilderProjectCommands
{
    public static int New(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "amsetup.project.json";
        if (File.Exists(path)) throw new IOException($"Project already exists: {path}");

        var project = new SetupBuilderProject();
        var manifest = SetupManifest.CreateDefault("My Application");
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(baseDir);
        WriteProject(path, project);
        string manifestPath = Resolve(baseDir, project.ManifestPath);
        if (!File.Exists(manifestPath))
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, AmSetupJsonContext.Default.SetupManifest) + Environment.NewLine, Encoding.UTF8);

        Console.WriteLine($"Created builder project: {path}");
        return 0;
    }

    public static int Build(string[] args)
    {
        string projectPath = Program.ReadOption(args, "--project", "amsetup.project.json");
        bool allowFrameworkStub = args.Contains("--allow-framework-dependent-stub", StringComparer.OrdinalIgnoreCase);
        var state = Load(projectPath);
        var result = BuildProject(projectPath, state.Project, state.Manifest, allowFrameworkStub);
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    public static BuilderState Load(string projectPath)
    {
        if (!File.Exists(projectPath))
        {
            var defaultProject = new SetupBuilderProject();
            return State(Path.GetFullPath(projectPath), defaultProject, SetupManifest.CreateDefault("My Application"));
        }

        string fullProject = Path.GetFullPath(projectPath);
        string baseDir = Path.GetDirectoryName(fullProject) ?? ".";
        var project = JsonSerializer.Deserialize(File.ReadAllText(fullProject, Encoding.UTF8), AmSetupJsonContext.Default.SetupBuilderProject)
            ?? new SetupBuilderProject();
        string manifestPath = Resolve(baseDir, project.ManifestPath);
        var manifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(manifestPath, Encoding.UTF8), AmSetupJsonContext.Default.SetupManifest) ?? SetupManifest.CreateDefault("My Application")
            : SetupManifest.CreateDefault("My Application");
        return State(fullProject, project, manifest);
    }

    private static BuilderState State(string projectFile, SetupBuilderProject project, SetupManifest manifest) =>
        new(projectFile, project, manifest, BuiltInAssets.DefaultIconPath(), BuiltInAssets.DefaultSplashPath());

    public static string DefaultInteractiveProjectPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("AMSETUP_BUILDER_PROJECT");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Directory.GetCurrentDirectory();

        return Path.Combine(documents, "amSetup Projects", "New Setup", "amsetup.project.json");
    }

    public static void EnsureExists(string projectPath)
    {
        if (File.Exists(projectPath)) return;

        string fullProject = Path.GetFullPath(projectPath);
        string baseDir = Path.GetDirectoryName(fullProject) ?? ".";
        Directory.CreateDirectory(baseDir);

        var project = new SetupBuilderProject
        {
            PayloadPath = "payload",
            OutputPath = OperatingSystem.IsWindows() ? "dist/Setup.exe" : "dist/Setup"
        };
        WriteProject(fullProject, project);

        string payload = Resolve(baseDir, project.PayloadPath);
        Directory.CreateDirectory(payload);

        string manifestPath = Resolve(baseDir, project.ManifestPath);
        if (!File.Exists(manifestPath))
        {
            var manifest = SetupManifest.CreateDefault("My Application");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, AmSetupJsonContext.Default.SetupManifest) + Environment.NewLine, Encoding.UTF8);
        }
    }

    public static void Save(string projectPath, SetupBuilderProject project, SetupManifest manifest)
    {
        string fullProject = Path.GetFullPath(projectPath);
        string baseDir = Path.GetDirectoryName(fullProject) ?? ".";
        Directory.CreateDirectory(baseDir);
        WriteProject(fullProject, project);
        string manifestPath = Resolve(baseDir, project.ManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath) ?? ".");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest.NormalizeForCurrentHost(), AmSetupJsonContext.Default.SetupManifest) + Environment.NewLine, Encoding.UTF8);
    }

    public static BuilderBuildResult BuildProject(string projectPath, SetupBuilderProject project, SetupManifest manifest, bool allowFrameworkStub)
    {
        string fullProject = Path.GetFullPath(projectPath);
        string baseDir = Path.GetDirectoryName(fullProject) ?? ".";
        string payload = Resolve(baseDir, project.PayloadPath);
        string output = Resolve(baseDir, project.OutputPath);
        string stub = string.IsNullOrWhiteSpace(project.StubPath)
            ? Program.ResolveDefaultStubPath()
            : Resolve(baseDir, project.StubPath);

        if (!Directory.Exists(payload))
            return new BuilderBuildResult(false, $"Payload directory does not exist: {payload}", output);
        if (!File.Exists(stub))
            return new BuilderBuildResult(false, $"Stub does not exist: {stub}", output);

        Program.EnsureProductionStub(stub, allowFrameworkStub);
        long chunkSize = string.IsNullOrWhiteSpace(project.ChunkSize) ? 0 : SizeParser.Parse(project.ChunkSize);
        string manifestBase = Path.GetDirectoryName(Resolve(baseDir, project.ManifestPath)) ?? baseDir;
        var packagedManifest = BrandingProcessor.PrepareForPackage(manifest.NormalizeForCurrentHost(), manifestBase);
        PackageBuilder.WriteInstaller(stub, output, payload, packagedManifest, project.Compression, project.Layout, chunkSize);
        return new BuilderBuildResult(true, $"Built installer: {output}", output);
    }

    private static void WriteProject(string path, SetupBuilderProject project)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(project, AmSetupJsonContext.Default.SetupBuilderProject) + Environment.NewLine, Encoding.UTF8);
    }

    private static string Resolve(string baseDir, string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(baseDir, path));
}

internal static class SetupBuilderUi
{
    public static int Run(string[] args)
    {
        string projectPath = Program.ReadOption(args, "--project", args.Length == 0 ? BuilderProjectCommands.DefaultInteractiveProjectPath() : "amsetup.project.json");
        string defaultPort = Environment.GetEnvironmentVariable("AMSETUP_BUILDER_PORT") ?? "41873";
        int port = int.Parse(Program.ReadOption(args, "--port", defaultPort));
        bool noBrowser = args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase) ||
            Environment.GetEnvironmentVariable("AMSETUP_NO_BROWSER") == "1";

        BuilderProjectCommands.EnsureExists(projectPath);

        string prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        Console.WriteLine($"amSetup Builder UI: {prefix}");
        Console.WriteLine("Press Ctrl+C to stop.");
        if (!noBrowser) OpenBrowser(prefix);

        while (listener.IsListening)
        {
            var context = listener.GetContext();
            Handle(context, projectPath);
        }

        return 0;
    }

    private static void Handle(HttpListenerContext context, string defaultProject)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            if (context.Request.HttpMethod == "GET" && path == "/")
            {
                WriteText(context, Html, "text/html; charset=utf-8");
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/state")
            {
                string project = context.Request.QueryString["project"] ?? defaultProject;
                WriteJson(context, BuilderProjectCommands.Load(project), AmSetupJsonContext.Default.BuilderState);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/browse")
            {
                string browsePath = context.Request.QueryString["path"] ?? "";
                string kind = context.Request.QueryString["kind"] ?? "folder";
                WriteJson(context, LocalBrowser.Browse(browsePath, kind), AmSetupJsonContext.Default.BrowseResult);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/payload-files")
            {
                string project = context.Request.QueryString["project"] ?? defaultProject;
                string payload = context.Request.QueryString["payload"] ?? "";
                WriteJson(context, LocalBrowser.PayloadFiles(project, payload), AmSetupJsonContext.Default.PayloadFileResult);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/save")
            {
                var request = ReadJson(context, AmSetupJsonContext.Default.BuilderSaveRequest);
                BuilderProjectCommands.Save(request.ProjectFile, request.Project, request.Manifest);
                WriteJson(context, BuilderProjectCommands.Load(request.ProjectFile), AmSetupJsonContext.Default.BuilderState);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/analyze")
            {
                var request = ReadJson(context, AmSetupJsonContext.Default.BuilderAnalyzeRequest);
                string baseDir = Path.GetDirectoryName(Path.GetFullPath(request.ProjectFile)) ?? Directory.GetCurrentDirectory();
                string payload = Path.IsPathRooted(request.Project.PayloadPath) ? request.Project.PayloadPath : Path.GetFullPath(Path.Combine(baseDir, request.Project.PayloadPath));
                var report = DependencyAnalyzer.Analyze(payload, request.Manifest);
                WriteJson(context, report, AmSetupJsonContext.Default.DependencyReport);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/build")
            {
                var request = ReadJson(context, AmSetupJsonContext.Default.BuilderBuildRequest);
                BuilderProjectCommands.Save(request.ProjectFile, request.Project, request.Manifest);
                var result = BuilderProjectCommands.BuildProject(request.ProjectFile, request.Project, request.Manifest, request.AllowFrameworkDependentStub);
                WriteJson(context, result, AmSetupJsonContext.Default.BuilderBuildResult);
                return;
            }

            context.Response.StatusCode = 404;
            WriteText(context, "Not found", "text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            WriteText(context, ex.Message, "text/plain; charset=utf-8");
        }
    }

    private static T ReadJson<T>(HttpListenerContext context, JsonTypeInfo<T> info)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        string text = reader.ReadToEnd();
        return JsonSerializer.Deserialize(text, info) ?? throw new InvalidDataException("Invalid JSON request.");
    }

    private static void WriteJson<T>(HttpListenerContext context, T value, JsonTypeInfo<T> info)
    {
        string json = JsonSerializer.Serialize(value, info);
        WriteText(context, json, "application/json; charset=utf-8");
    }

    private static void WriteText(HttpListenerContext context, string text, string contentType)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch
        {
            Console.WriteLine("Open the URL above in a browser.");
        }
    }

    private const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>amSetup Builder</title>
<style>
:root{color-scheme:dark;--bg:#101315;--band:#151a1d;--panel:#1c2226;--line:#303a41;--text:#edf2f5;--muted:#9aa7ae;--accent:#41c7a5;--accent2:#f0b84d;--warn:#f7c96b;--err:#ff6b6b}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:Segoe UI,Roboto,Arial,sans-serif;font-size:14px}
header{height:60px;border-bottom:1px solid var(--line);display:flex;align-items:center;gap:14px;padding:0 18px;background:var(--band)}
h1{font-size:18px;margin:0;font-weight:650}.sub{color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.toolbar{display:flex;gap:8px;margin-left:auto}
button{background:#243039;border:1px solid #3b4850;color:var(--text);padding:8px 12px;border-radius:6px;cursor:pointer;font:inherit}button.primary{background:#13785f;border-color:#2db392}button.gold{background:#6e521e;border-color:#b1832c}button:hover{filter:brightness(1.12)}
main{display:grid;grid-template-columns:330px 1fr;min-height:calc(100vh - 60px)}
aside{border-right:1px solid var(--line);padding:16px;background:#171d20;overflow:auto}
section{padding:16px;overflow:auto}.group{border-bottom:1px solid var(--line);padding-bottom:14px;margin-bottom:14px}
h2{font-size:13px;text-transform:uppercase;color:var(--muted);letter-spacing:.05em;margin:0 0 10px}h3{font-size:15px;margin:0 0 10px}
label{display:block;color:var(--muted);margin:9px 0 5px}input,select,textarea{width:100%;background:#0f1315;border:1px solid #344047;color:var(--text);border-radius:6px;padding:8px;font:inherit}
input[type=checkbox]{width:auto}.grid{display:grid;grid-template-columns:1fr 1fr;gap:12px}.wide{grid-column:1/-1}
.workspace{display:grid;grid-template-columns:minmax(420px,680px) minmax(360px,1fr);gap:16px}.panel{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:14px;margin-bottom:14px}
.steps{display:grid;gap:8px}.step{display:grid;grid-template-columns:32px 1fr;gap:10px;align-items:start;background:#20272b;border:1px solid var(--line);border-radius:8px;padding:10px}.num{width:28px;height:28px;border-radius:50%;display:grid;place-items:center;background:#263941;color:var(--accent);font-weight:700}
.status{white-space:pre-wrap;background:#101416;border:1px solid var(--line);border-radius:6px;padding:10px;min-height:44px;color:var(--muted)}
.summary{display:flex;gap:10px;flex-wrap:wrap;margin-bottom:12px}.pill{background:#20272b;border:1px solid var(--line);border-radius:999px;padding:6px 10px;color:var(--muted)}
.issues{display:grid;gap:8px}.issue{border-left:4px solid var(--line);background:#171d20;padding:10px;border-radius:4px}.issue.error{border-color:var(--err)}.issue.warning{border-color:var(--warn)}.issue.info{border-color:var(--accent)}
details{background:#171d20;border:1px solid var(--line);border-radius:8px;padding:10px}summary{cursor:pointer;color:var(--muted)}textarea{min-height:360px;font-family:Consolas,ui-monospace,monospace;resize:vertical}
.preview{display:grid;grid-template-columns:72px 1fr;gap:12px;align-items:center}.iconbox{width:64px;height:64px;border-radius:8px;background:linear-gradient(135deg,#203138,#101315);border:1px solid var(--line);display:grid;place-items:center;color:var(--accent2);font-size:28px;font-weight:800}
.pathrow{display:grid;grid-template-columns:1fr auto;gap:8px}.picker,.previewModal{position:fixed;inset:8vh 10vw;background:#151a1d;border:1px solid var(--line);border-radius:8px;box-shadow:0 18px 80px #000b;z-index:10;padding:14px;display:none;grid-template-rows:auto auto 1fr auto;gap:10px}.picker.open,.previewModal.open{display:grid}.picklist{overflow:auto;border:1px solid var(--line);border-radius:6px;background:#0f1315}.pickitem{display:grid;grid-template-columns:28px 1fr;gap:8px;padding:8px 10px;border-bottom:1px solid #20282d;cursor:pointer}.pickitem:hover{background:#20272b}.pickbar{display:grid;grid-template-columns:1fr auto auto;gap:8px}.selectrow{display:grid;grid-template-columns:1fr auto;gap:8px}.registry,.dirrow{display:grid;grid-template-columns:90px 1fr 120px;gap:8px;align-items:end}.setupPreview{background:#0f1315;border:1px solid var(--line);border-radius:8px;overflow:hidden;max-width:720px}.setupHead{padding:16px 18px;font-size:20px;font-weight:700}.setupBody{padding:18px}.setupSplash{display:grid;place-items:center;min-height:130px;border:1px dashed #3a464e;border-radius:6px;margin-bottom:14px;color:var(--muted)}.progressPreview{height:18px;background:#222b30;border-radius:999px;overflow:hidden}.progressPreview div{height:100%;width:62%;background:var(--accent)}.componentPreview{display:grid;gap:6px;margin:12px 0}.folderList{display:grid;gap:6px}
@media(max-width:1050px){main{grid-template-columns:1fr}.workspace{grid-template-columns:1fr}aside{border-right:0;border-bottom:1px solid var(--line)}} 
</style>
</head>
<body>
<header><h1>amSetup Builder</h1><div id="projectName" class="sub"></div><div class="toolbar"><button onclick="loadState()">Reload</button><button onclick="previewInstaller()">Preview</button><button onclick="save()">Save</button><button onclick="analyze()">Analyze</button><button class="primary" onclick="build()">Build Installer</button></div></header>
<main>
<aside>
<div class="group"><h2>Build Flow</h2><div class="steps"><div class="step"><div class="num">1</div><div>Set product name and install folder.</div></div><div class="step"><div class="num">2</div><div>Point payload to your published app folder.</div></div><div class="step"><div class="num">3</div><div>Analyze dependencies, then build.</div></div></div></div>
<div class="group"><h2>Project</h2><label>Project file</label><input id="projectFile"><label>Manifest</label><input id="manifestPath"><label>Payload folder</label><div class="pathrow"><input id="payloadPath"><button onclick="openPicker('payloadPath','folder')">Browse</button></div><label>Output installer</label><input id="outputPath"><label>Stub executable</label><div class="pathrow"><input id="stubPath" placeholder="empty = this amSetup executable"><button onclick="openPicker('stubPath','exe')">Browse</button></div></div>
<div class="group"><h2>Package</h2><div class="grid"><div><label>Compression</label><select id="compression"><option>Fastest</option><option selected>Balanced</option><option>Smallest</option><option>Store</option></select></div><div><label>Layout</label><select id="layout"><option>Embedded</option><option>External</option><option>Split</option></select></div></div><label>Chunk size</label><input id="chunkSize" value="512m"><label><input id="allowFramework" type="checkbox"> allow local dev stub</label></div>
<div class="group"><h2>Status</h2><div id="status" class="status">Ready.</div></div>
</aside>
<section>
<div class="workspace">
<div>
<div class="panel"><h3>Product</h3><div class="grid"><div><label>Name</label><input id="productName"></div><div><label>Version</label><input id="version"></div><div><label>Identifier</label><input id="identifier"></div><div><label>Publisher</label><input id="publisher"></div><div class="wide"><label>Description</label><input id="description"></div><div class="wide"><label>Default install folder</label><input id="defaultInstallDirectory"></div><div class="wide"><label>License text</label><textarea id="licenseText" style="min-height:80px"></textarea></div></div><label><input id="requireConfirmation" type="checkbox"> ask before install</label></div>
<div class="panel"><h3>Window Designer</h3><div class="grid"><div><label>Title</label><input id="windowTitle"></div><div><label>Style</label><select id="windowStyle"><option>classic</option><option>modern</option><option>compact</option></select></div><div class="wide"><label>Subtitle</label><input id="windowSubtitle"></div><div class="wide"><label>Intro text</label><input id="windowIntro"></div><div class="wide"><label>Footer text</label><input id="windowFooter"></div><div><label>Width</label><input id="windowWidth" value="720"></div><div><label>Height</label><input id="windowHeight" value="460"></div></div><label><input id="windowSidebar" type="checkbox"> show sidebar</label></div>
<div class="panel"><h3>Branding</h3><div class="preview"><div class="iconbox">AS</div><div><label>Setup icon (.ico)</label><div class="pathrow"><input id="iconPath"><button onclick="openPicker('iconPath','icon')">Browse</button></div><button onclick="useDefaultIcon()">Use oldschool icon</button></div></div><div class="grid"><div class="wide"><label>Splash image (.png/.jpg)</label><div class="pathrow"><input id="splashPath"><button onclick="openPicker('splashPath','image')">Browse</button></div></div><div><label>Splash duration</label><input id="splashDuration" value="1500"></div><div><label>Banner style</label><select id="banner"><option>classic</option><option>minimal</option></select></div></div><label><input id="showSplash" type="checkbox"> show splash when setup launches</label><button onclick="useDefaultSplash()">Use icon as splash</button></div>
<div class="panel"><h3>Shortcut</h3><label><input id="createShortcut" type="checkbox"> create desktop/start shortcut</label><div class="grid"><div class="wide"><label>Executable from payload</label><div class="selectrow"><select id="shortcutExecutable"></select><button onclick="loadExecutables()">Refresh</button></div></div><div class="wide"><label>Shortcut target inside install folder</label><input id="shortcutTarget" placeholder="{InstallDir}/MyApp.exe"></div><div class="wide"><label>Working directory</label><input id="shortcutWorkingDirectory" placeholder="{InstallDir}"></div><div><label>Name</label><input id="shortcutName"></div><div><label>Location</label><select id="shortcutLocation"><option>desktop</option><option>startMenu</option><option>applications</option><option>install</option></select></div></div></div>
<div class="panel"><h3>Install Folders</h3><div class="dirrow"><div><label>OS</label><select id="installDirOs"><option>any</option><option>windows</option><option>linux</option><option>macos</option></select></div><div><label>Folder inside install directory</label><input id="installDirPath" placeholder="data"></div><div><button onclick="addInstallDir()">Add</button></div></div><div style="margin-top:8px"><button onclick="quickDir('data')">data</button> <button onclick="quickDir('config')">config</button> <button onclick="quickDir('plugins')">plugins</button> <button onclick="quickDir('logs')">logs</button></div><div id="installDirList" class="status" style="margin-top:10px">No extra folders.</div></div>
<div class="panel"><h3>Registry</h3><div class="registry"><div><label>Root</label><select id="registryRoot"><option>HKCU</option><option>HKLM</option></select></div><div><label>Key</label><input id="registryKey" placeholder="Software\\Company\\Product"></div><div><label>Kind</label><select id="registryKind"><option>String</option><option>ExpandString</option><option>DWord</option><option>QWord</option><option>MultiString</option></select></div><div><label>Name</label><input id="registryName" placeholder="InstallDir"></div><div><label>Value</label><input id="registryValue" placeholder="{InstallDir}"></div><div><button onclick="addRegistry()">Add</button></div></div><div id="registryList" class="status">No registry edits.</div></div>
<details><summary>Advanced manifest JSON</summary><textarea id="manifest"></textarea></details>
</div>
<div>
<div class="panel"><h3>Dependency Analysis</h3><div id="summary" class="summary"></div><div id="issues" class="issues"><div class="status">Run Analyze after selecting your payload folder.</div></div></div>
<div class="panel"><h3>Prerequisites</h3><div id="prerequisiteList" class="status">No prerequisites detected yet.</div></div>
<div class="panel"><h3>Theme</h3><label>Theme preset</label><select id="themePreset" onchange="applyThemePreset()"><option value="oldschool">Oldschool Setup</option><option value="dark">Dark Modern</option><option value="amber">Amber Terminal</option><option value="blue">Classic Blue</option><option value="custom">Custom</option></select><div class="grid"><div><label>Header color</label><input id="headerColor"></div><div><label>Accent color</label><input id="accentColor"></div><div><label>Progress color</label><input id="progressColor"></div></div></div>
</div>
</div>
</section>
</main>
<div id="picker" class="picker"><h3 id="pickerTitle">Browse</h3><div class="pickbar"><input id="pickerPath"><button onclick="browsePath()">Go</button><button onclick="closePicker()">Close</button></div><div id="pickerList" class="picklist"></div><div><button onclick="chooseCurrentFolder()">Use current folder</button></div></div>
<div id="previewModal" class="previewModal"><h3>Installer Preview</h3><div id="previewContent"></div><div><button onclick="closePreview()">Close</button></div></div>
<script>
const $=id=>document.getElementById(id);let current={};
let pickerTarget='',pickerKind='folder',pickerCurrent='';
async function api(url,body){const r=await fetch(url,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)});if(!r.ok)throw new Error(await r.text());return await r.json();}
function project(){return{manifestPath:$('manifestPath').value,payloadPath:$('payloadPath').value,outputPath:$('outputPath').value,stubPath:$('stubPath').value,compression:$('compression').value,layout:$('layout').value,chunkSize:$('chunkSize').value};}
function baseManifest(){try{return JSON.parse($('manifest').value||'{}');}catch{return current.manifest||{};}}
function manifest(){const m=baseManifest();m.productName=$('productName').value;m.identifier=$('identifier').value;m.version=$('version').value;m.publisher=$('publisher').value;m.description=$('description').value;m.defaultInstallDirectory=$('defaultInstallDirectory').value;m.licenseText=$('licenseText').value;m.requireConfirmation=$('requireConfirmation').checked;m.theme=m.theme||{};m.theme.headerColor=$('headerColor').value;m.theme.accentColor=$('accentColor').value;m.theme.progressColor=$('progressColor').value;m.theme.banner=$('banner').value;m.window={title:$('windowTitle').value,subtitle:$('windowSubtitle').value,introText:$('windowIntro').value,footerText:$('windowFooter').value,style:$('windowStyle').value,width:parseInt($('windowWidth').value||'720',10),height:parseInt($('windowHeight').value||'460',10),showSidebar:$('windowSidebar').checked};m.branding=m.branding||{};m.branding.iconPath=$('iconPath').value;m.branding.splashPath=$('splashPath').value;m.branding.showSplash=$('showSplash').checked;m.branding.splashDurationMilliseconds=parseInt($('splashDuration').value||'1500',10);if($('createShortcut').checked){m.shortcuts=[{os:'any',name:$('shortcutName').value||m.productName,target:$('shortcutTarget').value,arguments:'',location:$('shortcutLocation').value,workingDirectory:$('shortcutWorkingDirectory').value||'{InstallDir}'}];}else{m.shortcuts=[];}m.installDirectories=current.installDirectories||[];m.registryValues=current.registryValues||[];m.prerequisites=current.prerequisites||m.prerequisites||[];syncJson(m);return m;}
function fill(s){current=s;const m=s.manifest||{};const b=m.branding||{};const t=m.theme||{};const w=m.window||{};const sc=(m.shortcuts||[])[0]||{};current.registryValues=m.registryValues||[];current.installDirectories=m.installDirectories||[];current.prerequisites=m.prerequisites||[];$('projectFile').value=s.projectFile;$('projectName').textContent=s.projectFile;$('manifestPath').value=s.project.manifestPath;$('payloadPath').value=s.project.payloadPath;$('outputPath').value=s.project.outputPath;$('stubPath').value=s.project.stubPath;$('compression').value=s.project.compression;$('layout').value=s.project.layout;$('chunkSize').value=s.project.chunkSize;$('productName').value=m.productName||'';$('identifier').value=m.identifier||'';$('version').value=m.version||'';$('publisher').value=m.publisher||'';$('description').value=m.description||'';$('defaultInstallDirectory').value=m.defaultInstallDirectory||'';$('licenseText').value=m.licenseText||'';$('requireConfirmation').checked=m.requireConfirmation!==false;$('windowTitle').value=w.title||'{ProductName} Setup';$('windowSubtitle').value=w.subtitle||'Install {ProductName} {Version}';$('windowIntro').value=w.introText||'This setup will install {ProductName} on your computer.';$('windowFooter').value=w.footerText||'Powered by amSetup';$('windowStyle').value=w.style||'classic';$('windowWidth').value=w.width||720;$('windowHeight').value=w.height||460;$('windowSidebar').checked=w.showSidebar!==false;$('headerColor').value=t.headerColor||'white';$('accentColor').value=t.accentColor||'cyan';$('progressColor').value=t.progressColor||'green';$('banner').value=t.banner||'classic';$('themePreset').value=detectTheme(t);$('iconPath').value=b.iconPath||'';$('splashPath').value=b.splashPath||'';$('showSplash').checked=!!b.showSplash;$('splashDuration').value=b.splashDurationMilliseconds||1500;$('createShortcut').checked=!!sc.target;$('shortcutTarget').value=sc.target||'';$('shortcutWorkingDirectory').value=sc.workingDirectory||'{InstallDir}';$('shortcutName').value=sc.name||m.productName||'';$('shortcutLocation').value=sc.location||'desktop';renderInstallDirs();renderRegistry();renderPrerequisites();syncJson(m);loadExecutables(false);}
function syncJson(m){$('manifest').value=JSON.stringify(m,null,2);}
function useDefaultIcon(){$('iconPath').value=current.defaultIconPath||'';setStatus('Default oldschool icon selected.');}
function useDefaultSplash(){$('splashPath').value=current.defaultSplashPath||'';$('showSplash').checked=true;setStatus('Default splash selected.');}
const themes={oldschool:{headerColor:'white',accentColor:'cyan',progressColor:'green',banner:'classic'},dark:{headerColor:'gray',accentColor:'cyan',progressColor:'green',banner:'minimal'},amber:{headerColor:'yellow',accentColor:'yellow',progressColor:'yellow',banner:'classic'},blue:{headerColor:'white',accentColor:'blue',progressColor:'cyan',banner:'classic'}};
function applyThemePreset(){const p=$('themePreset').value;if(!themes[p])return;const t=themes[p];$('headerColor').value=t.headerColor;$('accentColor').value=t.accentColor;$('progressColor').value=t.progressColor;$('banner').value=t.banner;setStatus('Theme preset applied.');}
function detectTheme(t){for(const [k,v] of Object.entries(themes)){if((t.headerColor||'')==v.headerColor&&(t.accentColor||'')==v.accentColor&&(t.progressColor||'')==v.progressColor&&(t.banner||'')==v.banner)return k;}return 'custom';}
function addRegistry(){const item={os:'windows',root:$('registryRoot').value,key:$('registryKey').value,name:$('registryName').value,value:$('registryValue').value,valueKind:$('registryKind').value,ignoreFailure:false};if(!item.key){setStatus('Registry key is required.');return;}current.registryValues=current.registryValues||[];current.registryValues.push(item);renderRegistry();manifest();setStatus('Registry edit added.');}
function removeRegistry(i){current.registryValues.splice(i,1);renderRegistry();manifest();}
function renderRegistry(){const list=current.registryValues||[];$('registryList').innerHTML=list.length?list.map((r,i)=>`${escapeHtml(r.root)}\\${escapeHtml(r.key)} ${escapeHtml(r.name||'(Default)')} = ${escapeHtml(r.value)} <button onclick="removeRegistry(${i})">Remove</button>`).join('<br>'):'No registry edits.';}
function addInstallDir(){const p=$('installDirPath').value.trim();if(!p){setStatus('Folder path is required.');return;}current.installDirectories=current.installDirectories||[];current.installDirectories.push({os:$('installDirOs').value,path:p});$('installDirPath').value='';renderInstallDirs();manifest();setStatus('Install folder added.');}
function quickDir(p){$('installDirPath').value=p;addInstallDir();}
function removeInstallDir(i){current.installDirectories.splice(i,1);renderInstallDirs();manifest();}
function renderInstallDirs(){const list=current.installDirectories||[];$('installDirList').innerHTML=list.length?list.map((d,i)=>`${escapeHtml(d.os)}: ${escapeHtml(d.path)} <button onclick="removeInstallDir(${i})">Remove</button>`).join('<br>'):'No extra folders.';}
function renderPrerequisites(){const list=current.prerequisites||[];$('prerequisiteList').innerHTML=list.length?list.map(p=>`<b>${escapeHtml(p.name||p.id)}</b><br>${escapeHtml(p.message||p.kind||'required')}<br><small>${escapeHtml(p.downloadUrl||p.bundledPath||'')}</small>`).join('<hr>'):'No prerequisites detected yet.';}
function previewInstaller(){const m=manifest();const tc=cssColor(m.theme?.headerColor,'#edf2f5'),ac=cssColor(m.theme?.accentColor,'#41c7a5'),pc=cssColor(m.theme?.progressColor,'#41c7a5');const dirs=(m.installDirectories||[]).map(d=>`<div>[folder] ${escapeHtml(d.path)}</div>`).join('')||'<div>No extra folders</div>';const comps=(m.components||[]).map(c=>`<label><input type="checkbox" checked disabled> ${escapeHtml(c.name||c.id)} ${c.required?'(required)':''}</label>`).join('')||'<label><input type="checkbox" checked disabled> Main Files</label>';const prereqs=(m.prerequisites||[]).map(p=>`<div>${escapeHtml(p.name||p.id)}</div>`).join('')||'<div>No prerequisites</div>';const title=expandTemplate(m.window?.title||'{ProductName} Setup',m),sub=expandTemplate(m.window?.subtitle||'',m),intro=expandTemplate(m.window?.introText||'',m),foot=expandTemplate(m.window?.footerText||'',m);$('previewContent').innerHTML=`<div class="setupPreview" style="max-width:${m.window?.width||720}px;min-height:${m.window?.height||460}px"><div class="setupHead" style="color:${tc};background:#20272b">${escapeHtml(title)}</div><div class="setupBody"><h3>${escapeHtml(sub)}</h3><p>${escapeHtml(intro)}</p><div class="setupSplash">${m.branding?.showSplash?'Splash: '+escapeHtml(m.branding.splashPath||'embedded image'):'No splash'}</div><div><b>Install folder</b><br>${escapeHtml(m.defaultInstallDirectory||'{InstallDir}')}</div><div class="componentPreview">${comps}</div><div><b>Prerequisites</b>${prereqs}</div><div><b>Created folders</b><div class="folderList">${dirs}</div></div><p style="color:${ac}">Shortcut: ${escapeHtml((m.shortcuts||[])[0]?.target||'none')}</p><div class="progressPreview"><div style="background:${pc}"></div></div><small>${escapeHtml(foot)}</small></div></div>`;$('previewModal').classList.add('open');}
function expandTemplate(s,m){return String(s||'').replaceAll('{ProductName}',m.productName||'').replaceAll('{Version}',m.version||'').replaceAll('{Identifier}',m.identifier||'');}
function closePreview(){$('previewModal').classList.remove('open');}
function cssColor(name,fallback){const map={white:'#edf2f5',gray:'#9aa7ae',cyan:'#41c7a5',green:'#58d26f',yellow:'#f0b84d',blue:'#5aa8ff',red:'#ff6b6b'};return map[String(name||'').toLowerCase()]||fallback;}
async function loadExecutables(showStatus=true){try{const url='/api/payload-files?project='+encodeURIComponent($('projectFile').value)+'&payload='+encodeURIComponent($('payloadPath').value);const r=await fetch(url);const data=await r.json();const items=data.executables.length?data.executables:data.files;$('shortcutExecutable').innerHTML='<option value="">Select executable...</option>'+items.map(f=>`<option value="${escapeHtml(f.path)}">${escapeHtml(f.path.replace('{InstallDir}/',''))}</option>`).join('');$('shortcutExecutable').onchange=()=>{if($('shortcutExecutable').value){$('shortcutTarget').value=$('shortcutExecutable').value;$('createShortcut').checked=true;}};if(showStatus)setStatus(`Found ${data.executables.length} executable candidate(s).`);}catch(e){if(showStatus)setStatus(e.message);}}
async function openPicker(target,kind){pickerTarget=target;pickerKind=kind;pickerCurrent=$(target).value||$('payloadPath').value||'';$('picker').classList.add('open');$('pickerTitle').textContent='Browse '+kind;await browsePath();}
function closePicker(){$('picker').classList.remove('open');}
async function browsePath(path){if(path)pickerCurrent=path;const q='/api/browse?kind='+encodeURIComponent(pickerKind)+'&path='+encodeURIComponent($('pickerPath').value||pickerCurrent);const r=await fetch(q);const b=await r.json();pickerCurrent=b.path;$('pickerPath').value=b.path;const entries=[...b.drives.map(x=>({...x,name:'Drive '+x.path})),{name:'..',path:b.parentPath,isDirectory:true},...b.directories,...b.files];$('pickerList').innerHTML=entries.map(e=>`<div class="pickitem" onclick="${e.isDirectory?`browsePath('${js(e.path)}')`:`choosePath('${js(e.path)}')`}"><div>${e.isDirectory?'[D]':'[F]'}</div><div>${escapeHtml(e.name)}<br><small>${escapeHtml(e.path)}</small></div></div>`).join('');}
function choosePath(path){$(pickerTarget).value=path;closePicker();if(pickerTarget==='payloadPath')loadExecutables();}
function chooseCurrentFolder(){choosePath(pickerCurrent);}
function js(s){return String(s).replace(/\\/g,'\\\\').replace(/'/g,"\\'");}
async function loadState(){try{const p=encodeURIComponent($('projectFile')?.value||'');const r=await fetch('/api/state'+(p?'?project='+p:''));fill(await r.json());setStatus('Loaded.');}catch(e){setStatus(e.message);}}
async function save(){try{const r=await api('/api/save',{projectFile:$('projectFile').value,project:project(),manifest:manifest()});fill(r);setStatus('Saved project and manifest.');}catch(e){setStatus(e.message);}}
async function analyze(){try{const r=await api('/api/analyze',{projectFile:$('projectFile').value,project:project(),manifest:manifest()});renderReport(r);fill({projectFile:$('projectFile').value,project:project(),manifest:r.suggestedManifest,defaultIconPath:current.defaultIconPath,defaultSplashPath:current.defaultSplashPath});current.prerequisites=r.prerequisites||current.prerequisites||[];renderPrerequisites();setStatus('Analysis complete. Suggested installer settings were applied.');}catch(e){setStatus(e.message);}}
async function build(){try{const r=await api('/api/build',{projectFile:$('projectFile').value,project:project(),manifest:manifest(),allowFrameworkDependentStub:$('allowFramework').checked});setStatus(r.message);}catch(e){setStatus(e.message);}}
function renderReport(r){$('summary').innerHTML=`<span class="pill">${r.totalFiles} files</span><span class="pill">${bytes(r.totalBytes)}</span><span class="pill">${r.dotNetApplications.length} .NET apps</span><span class="pill">${(r.prerequisites||[]).length} prereq(s)</span><span class="pill">${r.issues.length} issues</span>`;$('issues').innerHTML=r.issues.map(i=>`<div class="issue ${i.severity}"><b>${i.severity.toUpperCase()} ${i.code}</b><br>${escapeHtml(i.message)}${i.path?'<br><small>'+escapeHtml(i.path)+'</small>':''}</div>`).join('')||'<div class="status">No dependency issues found.</div>';}
function setStatus(t){$('status').textContent=t;}function bytes(n){if(n>1073741824)return(n/1073741824).toFixed(1)+' GB';if(n>1048576)return(n/1048576).toFixed(1)+' MB';if(n>1024)return(n/1024).toFixed(1)+' KB';return n+' B';}
function escapeHtml(s){return String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
loadState();
</script>
</body>
</html>
""";
}

internal static class LocalBrowser
{
    public static BrowseResult Browse(string path, string kind)
    {
        string current = NormalizeBrowsePath(path);
        string parent = Parent(current);
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new BrowseEntry(d.Name, d.RootDirectory.FullName, true))
            .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dirs = new List<BrowseEntry>();
        var files = new List<BrowseEntry>();
        if (Directory.Exists(current))
        {
            try
            {
                dirs = Directory.EnumerateDirectories(current)
                    .Select(d => new DirectoryInfo(d))
                    .Select(d => new BrowseEntry(d.Name, d.FullName, true))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(500)
                    .ToList();
            }
            catch
            {
            }

            if (!kind.Equals("folder", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    files = Directory.EnumerateFiles(current)
                        .Where(f => MatchesKind(f, kind))
                        .Select(f => new FileInfo(f))
                        .Select(f => new BrowseEntry(f.Name, f.FullName, false))
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(500)
                        .ToList();
                }
                catch
                {
                }
            }
        }

        return new BrowseResult(current, parent, drives, dirs, files);
    }

    public static PayloadFileResult PayloadFiles(string projectPath, string payloadPath)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Directory.GetCurrentDirectory();
        string payload = Path.IsPathRooted(payloadPath) ? Path.GetFullPath(payloadPath) : Path.GetFullPath(Path.Combine(baseDir, payloadPath));
        if (!Directory.Exists(payload)) return new PayloadFileResult([], []);

        var files = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(payload, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new BrowseEntry(Path.GetFileName(f), "{InstallDir}/" + f, false))
            .ToList();
        var executables = files
            .Where(f => IsExecutablePath(f.Path))
            .ToList();
        return new PayloadFileResult(executables, files);
    }

    private static string NormalizeBrowsePath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (File.Exists(full)) full = Path.GetDirectoryName(full) ?? full;
            if (Directory.Exists(full)) return full;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? Directory.GetCurrentDirectory() : home;
    }

    private static string Parent(string path)
    {
        var parent = Directory.GetParent(path);
        return parent?.FullName ?? path;
    }

    private static bool MatchesKind(string path, string kind) => kind.ToLowerInvariant() switch
    {
        "icon" => Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase),
        "image" => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
        "exe" => IsExecutablePath(path),
        _ => true
    };

    private static bool IsExecutablePath(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals("", StringComparison.OrdinalIgnoreCase);
    }
}
