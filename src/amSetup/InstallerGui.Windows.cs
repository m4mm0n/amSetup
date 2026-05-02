// SPDX-License-Identifier: GPL-3.0-or-later

#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AmSetup;

internal static partial class InstallerGui
{
    [SupportedOSPlatform("windows")]
    public static bool TryRun(AttachedPackage attached, string? target, string? componentArg, out int exitCode)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            exitCode = 0;
            return false;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        PackageArchive? archive = null;
        Exception? loadError = null;
        using (var loading = new PackageLoadingForm())
        {
            Task.Run(() =>
            {
                try
                {
                    archive = PackageStore.ReadPackage(attached, loading.Report);
                }
                catch (Exception ex)
                {
                    loadError = ex;
                }
                finally
                {
                    loading.Complete();
                }
            });
            Application.Run(loading);
        }

        if (loadError is not null)
        {
            MessageBox.Show(loadError.Message, "Setup package could not be loaded", MessageBoxButtons.OK, MessageBoxIcon.Error);
            exitCode = 1;
            return true;
        }

        if (archive is null)
        {
            exitCode = 1;
            return true;
        }

        target ??= PathTemplate.Expand(archive.Manifest.DefaultInstallDirectory, archive.Manifest);
        ShowSplash(archive.Manifest);

        using var form = new InstallerWizardForm(archive, target, componentArg);
        Application.Run(form);
        exitCode = form.ExitCode;
        return true;
    }

    private static void ShowSplash(SetupManifest manifest)
    {
        var branding = manifest.Branding;
        if (!branding.ShowSplash || string.IsNullOrWhiteSpace(branding.SplashImageBase64)) return;

        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(branding.SplashImageBase64));
            using var image = Image.FromStream(stream);
            Color transparentKey = Color.FromArgb(255, 1, 2, 3);
            using var splash = new Form
            {
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.None,
                ControlBox = false,
                ShowInTaskbar = false,
                Width = Math.Clamp(image.Width, 260, 900),
                Height = Math.Clamp(image.Height, 160, 520),
                BackColor = transparentKey,
                TransparencyKey = transparentKey
            };
            splash.Controls.Add(new PictureBox
            {
                Image = new Bitmap(image),
                SizeMode = PictureBoxSizeMode.Zoom,
                Dock = DockStyle.Fill,
                BackColor = transparentKey
            });
            var timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(branding.SplashDurationMilliseconds, 250, 10000) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                splash.Close();
            };
            splash.Shown += (_, _) => timer.Start();
            splash.FormClosed += (_, _) => timer.Dispose();
            splash.ShowDialog();
        }
        catch
        {
            Thread.Sleep(Math.Clamp(branding.SplashDurationMilliseconds, 250, 3000));
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class PackageLoadingForm : Form
{
    private readonly ProgressBar _progress = new();
    private readonly Label _stage = new();
    private bool _complete;

    public PackageLoadingForm()
    {
        Text = "Preparing Setup";
        Width = 420;
        Height = 138;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        ShowInTaskbar = true;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        _stage.Text = "Loading setup package...";
        _stage.Left = 18;
        _stage.Top = 18;
        _stage.Width = 366;
        _stage.Height = 24;
        _progress.Left = 18;
        _progress.Top = 52;
        _progress.Width = 366;
        _progress.Height = 23;
        Controls.AddRange([_stage, _progress]);
        Shown += (_, _) =>
        {
            if (_complete) Close();
        };
    }

    public void Report(PackageLoadProgress progress)
    {
        if (IsDisposed) return;
        void Update()
        {
            long total = progress.Total <= 0 ? 1 : progress.Total;
            int percent = (int)Math.Clamp(progress.Done * 100 / total, 0, 100);
            _progress.Value = percent;
            _stage.Text = $"{progress.Stage} ({percent}%)";
        }

        if (IsHandleCreated) BeginInvoke(Update);
    }

    public void Complete()
    {
        if (IsDisposed) return;
        _complete = true;
        if (IsHandleCreated) BeginInvoke(Close);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class InstallerWizardForm : Form
{
    private enum WizardPage
    {
        Welcome,
        License,
        Options,
        Installing,
        Complete
    }

    private readonly PackageArchive _archive;
    private readonly List<SetupComponent> _components;
    private readonly List<SetupShortcut> _shortcutChoices;
    private readonly Dictionary<string, CheckBox> _componentChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _shortcutChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly InstallerPalette _palette;
    private readonly Panel _sidebar = new();
    private readonly Panel _content = new();
    private readonly Panel _buttonPanel = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _body = new();
    private readonly TextBox _target = new();
    private readonly GlossyButton _browse = new();
    private readonly GlossyButton _back = new();
    private readonly GlossyButton _next = new();
    private readonly GlossyButton _cancel = new();
    private readonly GlossyProgressBar _progress = new();
    private readonly GlossyProgressBar _fileProgress = new();
    private readonly Label _progressText = new();
    private readonly Label _fileProgressText = new();
    private readonly ListBox _log = new();
    private readonly string? _componentArg;
    private WizardPage _page = WizardPage.Welcome;
    private bool _installStarted;
    private const int AboutSystemCommand = 0x1F10;

    public int ExitCode { get; private set; } = 2;

    public InstallerWizardForm(PackageArchive archive, string target, string? componentArg)
    {
        _archive = archive;
        _componentArg = componentArg;
        _components = Installer.ComponentsForDisplay(archive.Manifest);
        _shortcutChoices = Installer.ShortcutChoices(archive);

        var window = archive.Manifest.Window ?? new SetupWindow();
        _palette = InstallerPalette.Resolve(archive.Manifest.Theme, window);
        Text = Installer.ExpandWindowText(string.IsNullOrWhiteSpace(window.Title) ? "{ProductName} Setup" : window.Title, archive.Manifest, target);
        Width = Math.Clamp(window.Width, 640, 1100);
        Height = Math.Clamp(window.Height, 420, 780);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9F);
        BackColor = _palette.ContentBack;

        BuildLayout(window.ShowSidebar);
        InstallAboutContextMenu();
        _target.Text = target;
        Render();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        IntPtr menu = GetSystemMenu(Handle, false);
        if (menu != IntPtr.Zero)
        {
            AppendMenu(menu, 0x800, UIntPtr.Zero, null);
            AppendMenu(menu, 0, (UIntPtr)AboutSystemCommand, "About " + _archive.Manifest.ProductName);
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int wmSysCommand = 0x0112;
        if (m.Msg == wmSysCommand && ((int)m.WParam & 0xFFF0) == AboutSystemCommand)
        {
            ShowAbout();
            return;
        }

        base.WndProc(ref m);
    }

    private void BuildLayout(bool showSidebar)
    {
        _buttonPanel.Dock = DockStyle.Bottom;
        _buttonPanel.Height = 54;
        _buttonPanel.BackColor = _palette.FooterBack;
        _buttonPanel.Padding = new Padding(8);
        _cancel.Text = "Cancel";
        _cancel.Width = 92;
        _cancel.Dock = DockStyle.Left;
        _cancel.Click += (_, _) => Close();
        _next.Text = "Next >";
        _next.Width = 92;
        _next.Dock = DockStyle.Right;
        _next.Margin = new Padding(8, 0, 0, 0);
        _next.Click += (_, _) => Next();
        _back.Text = "< Back";
        _back.Width = 92;
        _back.Dock = DockStyle.Right;
        _back.Click += (_, _) => Back();
        _buttonPanel.Controls.AddRange([_cancel, _next, _back]);

        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = showSidebar ? 178 : 0;
        _sidebar.BackColor = _palette.Sidebar;
        _sidebar.Paint += PaintSidebar;

        _content.Dock = DockStyle.Fill;
        _content.Padding = new Padding(24, 20, 24, 12);
        _content.BackColor = _palette.ContentBack;
        _title.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
        _title.ForeColor = _palette.Title;
        _title.AutoSize = false;
        _title.Height = 34;
        _title.Dock = DockStyle.Top;
        _subtitle.AutoSize = false;
        _subtitle.Dock = DockStyle.Top;
        _subtitle.Height = 38;
        _subtitle.ForeColor = _palette.MutedText;
        _body.AutoSize = false;
        _body.Dock = DockStyle.Top;
        _body.Height = 88;
        _body.ForeColor = _palette.Text;

        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(_buttonPanel);
        StyleButton(_cancel, secondary: true);
        StyleButton(_back, secondary: true);
        StyleButton(_next, secondary: false);
    }

    private void InstallAboutContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = _palette.ContentBack,
            ForeColor = _palette.Text,
            Renderer = new ToolStripProfessionalRenderer(new InstallerMenuColors(_palette))
        };
        menu.Items.Add("About " + _archive.Manifest.ProductName, null, (_, _) => ShowAbout());
        ContextMenuStrip = menu;
        _content.ContextMenuStrip = menu;
        _sidebar.ContextMenuStrip = menu;
        _buttonPanel.ContextMenuStrip = menu;
    }

    private void ShowAbout()
    {
        using var about = new AboutSetupForm(_archive, _palette);
        about.ShowDialog(this);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr newItemId, string? newItem);

    private void Render()
    {
        _content.SuspendLayout();
        _content.Controls.Clear();
        _content.Controls.Add(_subtitle);
        _content.Controls.Add(_title);
        _back.Enabled = _page is WizardPage.License or WizardPage.Options;
        _cancel.Enabled = _page != WizardPage.Installing;
        _next.Enabled = _page != WizardPage.Installing;
        _next.Text = _page switch
        {
            WizardPage.Options => "Install",
            WizardPage.Complete => "Finish",
            _ => "Next >"
        };

        string target = string.IsNullOrWhiteSpace(_target.Text) ? "" : _target.Text;
        var window = _archive.Manifest.Window ?? new SetupWindow();
        _title.Text = PageTitle(window, target);
        _subtitle.Text = PageSubtitle(window, target);

        switch (_page)
        {
            case WizardPage.Welcome:
                RenderWelcome(window, target);
                break;
            case WizardPage.License:
                RenderLicense();
                break;
            case WizardPage.Options:
                RenderOptions();
                break;
            case WizardPage.Installing:
                RenderInstalling();
                break;
            case WizardPage.Complete:
                RenderComplete(window, target);
                break;
        }

        _content.ResumeLayout();
        ApplyTheme(_content);
        _sidebar.Invalidate();
    }

    private string PageTitle(SetupWindow window, string target) => _page switch
    {
        WizardPage.Welcome => Installer.ExpandWindowText(string.IsNullOrWhiteSpace(window.Title) ? "{ProductName} Setup" : window.Title, _archive.Manifest, target),
        WizardPage.License => "License Agreement",
        WizardPage.Options => "Choose Install Location",
        WizardPage.Installing => "Installing",
        WizardPage.Complete => "Completing Setup",
        _ => Text
    };

    private string PageSubtitle(SetupWindow window, string target) => _page switch
    {
        WizardPage.Welcome => Installer.ExpandWindowText(window.Subtitle, _archive.Manifest, target),
        WizardPage.License => "Please review the license terms before continuing.",
        WizardPage.Options => "Select the destination folder and optional components.",
        WizardPage.Installing => "Setup is installing files and configuring the application.",
        WizardPage.Complete => Installer.ExpandWindowText(window.FooterText, _archive.Manifest, target),
        _ => ""
    };

    private void RenderWelcome(SetupWindow window, string target)
    {
        _body.Text = Installer.ExpandWindowText(window.IntroText, _archive.Manifest, target) + Environment.NewLine + Environment.NewLine +
            $"Setup will install {_archive.Manifest.ProductName} { _archive.Manifest.Version }.";
        _body.Top = 98;
        _body.Left = 24;
        _body.Width = _content.ClientSize.Width - 48;
        _content.Controls.Add(_body);

        var details = new Label
        {
            Text = $"Publisher: {_archive.Manifest.Publisher}{Environment.NewLine}Files: {_archive.Entries.Count:N0}{Environment.NewLine}Package: {_archive.CompressedBytes:N0} bytes",
            AutoSize = false,
            Left = 24,
            Top = 205,
            Width = _content.ClientSize.Width - 48,
            Height = 70,
            ForeColor = _palette.MutedText
        };
        _content.Controls.Add(details);
    }

    private void RenderLicense()
    {
        var box = new RichTextBox
        {
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            WordWrap = true,
            Text = LicenseTextProcessor.PrepareForPackage(_archive.Manifest) ?? "",
            Left = 24,
            Top = 100,
            Width = _content.ClientSize.Width - 48,
            Height = _content.ClientSize.Height - 124,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Segoe UI", 9F)
        };
        _content.Controls.Add(box);
    }

    private void RenderOptions()
    {
        var targetLabel = new Label { Text = "Destination folder", Left = 24, Top = 98, Width = 300, Height = 20 };
        _target.Left = 24;
        _target.Top = 122;
        _target.Width = _content.ClientSize.Width - 150;
        _target.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _browse.Text = "Browse...";
        _browse.Left = _target.Right + 8;
        _browse.Top = 120;
        _browse.Width = 94;
        _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _browse.Click -= Browse;
        _browse.Click += Browse;
        _content.Controls.AddRange([targetLabel, _target, _browse]);

        var componentLabel = new Label { Text = "Components", Left = 24, Top = 165, Width = 300, Height = 22 };
        _content.Controls.Add(componentLabel);
        int top = 190;
        _componentChecks.Clear();
        var selected = Installer.ResolveComponents(_archive.Manifest, _componentArg, silent: true);
        foreach (var component in _components)
        {
            var check = new CheckBox
            {
                Text = string.IsNullOrWhiteSpace(component.Description) ? component.Name : $"{component.Name} - {component.Description}",
                Left = 28,
                Top = top,
                Width = _content.ClientSize.Width - 58,
                Height = 26,
                Checked = selected.Contains(component.Id),
                Enabled = !component.Required,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _componentChecks[component.Id] = check;
            _content.Controls.Add(check);
            top += 28;
        }

        if (_shortcutChoices.Count > 0)
        {
            top += 8;
            var shortcutLabel = new Label { Text = "Shortcuts", Left = 24, Top = top, Width = 300, Height = 22 };
            _content.Controls.Add(shortcutLabel);
            top += 25;
            _shortcutChecks.Clear();
            foreach (var shortcut in _shortcutChoices)
            {
                string key = shortcut.Location.ToLowerInvariant();
                var check = new CheckBox
                {
                    Text = ShortcutLabel(shortcut.Location),
                    Left = 28,
                    Top = top,
                    Width = _content.ClientSize.Width - 58,
                    Height = 26,
                    Checked = (_archive.Manifest.Shortcuts ?? []).Any(s => s.Location.Equals(shortcut.Location, StringComparison.OrdinalIgnoreCase)),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                _shortcutChecks[key] = check;
                _content.Controls.Add(check);
                top += 28;
            }
        }
    }

    private void RenderInstalling()
    {
        _progress.SetPalette(_palette);
        _fileProgress.SetPalette(_palette);
        _progress.Left = 24;
        _progress.Top = 104;
        _progress.Width = _content.ClientSize.Width - 48;
        _progress.Height = 24;
        _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _progressText.Left = 24;
        _progressText.Top = 138;
        _progressText.Width = _content.ClientSize.Width - 48;
        _progressText.Height = 24;
        _fileProgress.Left = 24;
        _fileProgress.Top = 168;
        _fileProgress.Width = _content.ClientSize.Width - 48;
        _fileProgress.Height = 20;
        _fileProgress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _fileProgressText.Left = 24;
        _fileProgressText.Top = 196;
        _fileProgressText.Width = _content.ClientSize.Width - 48;
        _fileProgressText.Height = 24;
        _log.Left = 24;
        _log.Top = 226;
        _log.Width = _content.ClientSize.Width - 48;
        _log.Height = _content.ClientSize.Height - 250;
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _content.Controls.AddRange([_progress, _progressText, _fileProgress, _fileProgressText, _log]);

        if (!_installStarted)
        {
            _installStarted = true;
            BeginInstall();
        }
    }

    private void RenderComplete(SetupWindow window, string target)
    {
        var message = new Label
        {
            Text = ExitCode == 0
                ? $"{_archive.Manifest.ProductName} has been installed successfully."
                : $"{_archive.Manifest.ProductName} setup did not complete.",
            Left = 24,
            Top = 112,
            Width = _content.ClientSize.Width - 48,
            Height = 70,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Regular)
        };
        _content.Controls.Add(message);

        string footer = Installer.ExpandWindowText(window.FooterText, _archive.Manifest, target);
        if (!string.IsNullOrWhiteSpace(footer))
        {
            _content.Controls.Add(new Label
            {
                Text = footer,
                Left = 24,
                Top = 190,
            Width = _content.ClientSize.Width - 48,
            Height = 44,
            ForeColor = _palette.MutedText
            });
        }
    }

    private void Browse(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _target.Text, Description = $"Choose where to install {_archive.Manifest.ProductName}" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _target.Text = dialog.SelectedPath;
    }

    private void Next()
    {
        if (_page == WizardPage.Complete)
        {
            Close();
            return;
        }

        if (_page == WizardPage.Options)
        {
            if (string.IsNullOrWhiteSpace(_target.Text))
            {
                MessageBox.Show(this, "Choose an install folder first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _page = WizardPage.Installing;
        }
        else if (_page == WizardPage.Welcome && !string.IsNullOrWhiteSpace(_archive.Manifest.LicenseText))
        {
            _page = WizardPage.License;
        }
        else
        {
            _page = WizardPage.Options;
        }
        Render();
    }

    private void Back()
    {
        _page = _page switch
        {
            WizardPage.Options when !string.IsNullOrWhiteSpace(_archive.Manifest.LicenseText) => WizardPage.License,
            WizardPage.Options => WizardPage.Welcome,
            WizardPage.License => WizardPage.Welcome,
            _ => _page
        };
        Render();
    }

    private void BeginInstall()
    {
        string target = _target.Text.Trim();
        var selected = _components
            .Where(c => c.Required || (_componentChecks.TryGetValue(c.Id, out var check) && check.Checked))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedShortcuts = _shortcutChoices
            .Where(s => _shortcutChecks.TryGetValue(s.Location.ToLowerInvariant(), out var check) && check.Checked)
            .ToList();
        var ui = new WinFormsInstallerUi(this);

        Task.Run(() =>
        {
            try
            {
                return Installer.InstallSelected(_archive, target, selected, dryRun: false, silent: true, ui, new InstallOptions(selectedShortcuts));
            }
            catch (Exception ex)
            {
                ui.Line("Error: " + ex.Message);
                return 1;
            }
        }).ContinueWith(task =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                ExitCode = task.Result;
                _page = WizardPage.Complete;
                Render();
            });
        });
    }

    private void PaintSidebar(object? sender, PaintEventArgs e)
    {
        using var sidebarBack = new LinearGradientBrush(_sidebar.ClientRectangle, _palette.SidebarGlossTop, _palette.Sidebar, LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(sidebarBack, _sidebar.ClientRectangle);
        using var titleBrush = new SolidBrush(_palette.SidebarTitle);
        using var mutedBrush = new SolidBrush(_palette.SidebarMuted);
        using var accentBrush = new SolidBrush(_palette.Accent);
        using var accentGloss = new LinearGradientBrush(new Rectangle(0, 0, 6, _sidebar.Height), _palette.AccentHover, _palette.AccentDown, LinearGradientMode.Vertical);
        using var titleFont = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        using var smallFont = new Font(Font.FontFamily, 8.5F);
        e.Graphics.FillRectangle(accentGloss, 0, 0, 6, _sidebar.Height);
        using var shine = new SolidBrush(Color.FromArgb(38, Color.White));
        e.Graphics.FillRectangle(shine, 6, 0, Math.Max(0, _sidebar.Width - 6), 64);
        e.Graphics.DrawString(_archive.Manifest.ProductName, titleFont, titleBrush, new RectangleF(18, 28, _sidebar.Width - 30, 70));
        e.Graphics.DrawString("Setup Wizard", smallFont, mutedBrush, new PointF(20, 102));

        string[] steps = ["Welcome", "License", "Options", "Install", "Finish"];
        for (int i = 0; i < steps.Length; i++)
        {
            int y = 160 + i * 34;
            bool current = (int)_page == i || (_page == WizardPage.Installing && i == 3);
            using var stepBrush = new SolidBrush(current ? _palette.SidebarTitle : _palette.SidebarMuted);
            if (current) e.Graphics.FillEllipse(accentBrush, 18, y - 2, 20, 20);
            e.Graphics.DrawString(current ? (i + 1).ToString() : " ", smallFont, titleBrush, new PointF(25, y));
            e.Graphics.DrawString(steps[i], smallFont, stepBrush, new PointF(46, y));
        }
    }

    private static string ShortcutLabel(string location) => location.ToLowerInvariant() switch
    {
        "desktop" => "Create a Desktop shortcut",
        "startmenu" => "Create a Start Menu shortcut",
        "applications" => "Create an Applications menu shortcut",
        "install" => "Create a launcher in the install folder",
        _ => "Create " + location + " shortcut"
    };

    private void ApplyTheme(Control root)
    {
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case RichTextBox richText:
                    richText.BackColor = _palette.InputBack;
                    richText.ForeColor = _palette.Text;
                    richText.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case TextBox text:
                    text.BackColor = _palette.InputBack;
                    text.ForeColor = _palette.Text;
                    text.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ListBox list:
                    list.BackColor = _palette.LogBack;
                    list.ForeColor = _palette.LogText;
                    list.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case CheckBox check:
                    check.BackColor = _palette.ContentBack;
                    check.ForeColor = check.Enabled ? _palette.Text : _palette.MutedText;
                    break;
                case Button button:
                    StyleButton(button, secondary: button != _next);
                    break;
                case Label label when label != _title && label != _subtitle:
                    label.BackColor = _palette.ContentBack;
                    if (label.ForeColor.ToArgb() == SystemColors.ControlText.ToArgb())
                        label.ForeColor = _palette.Text;
                    break;
            }

            if (control.HasChildren) ApplyTheme(control);
        }
    }

    private void StyleButton(Button button, bool secondary)
    {
        if (button is GlossyButton glossy)
            glossy.SetPalette(_palette, secondary);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = secondary ? _palette.ButtonBorder : _palette.Accent;
        button.FlatAppearance.MouseOverBackColor = secondary ? _palette.ButtonHover : _palette.AccentHover;
        button.FlatAppearance.MouseDownBackColor = _palette.AccentDown;
        button.BackColor = secondary ? _palette.SecondaryButtonBack : _palette.Accent;
        button.ForeColor = secondary ? _palette.ButtonText : _palette.AccentText;
    }

    private static Color ThemeColor(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.ToLowerInvariant() switch
        {
            "cyan" => Color.FromArgb(0, 153, 188),
            "green" => Color.FromArgb(0, 150, 95),
            "yellow" => Color.FromArgb(205, 143, 0),
            "blue" => Color.FromArgb(0, 120, 215),
            "white" => Color.White,
            "gray" or "grey" => Color.FromArgb(90, 96, 102),
            _ => Color.FromName(value)
        } is var named && named.ToArgb() != 0 ? named : fallback;
    }

    private sealed record InstallerPalette(
        Color ContentBack,
        Color FooterBack,
        Color Sidebar,
        Color Title,
        Color Text,
        Color MutedText,
        Color SidebarTitle,
        Color SidebarMuted,
        Color Accent,
        Color AccentHover,
        Color AccentDown,
        Color AccentText,
        Color SecondaryButtonBack,
        Color ButtonText,
        Color ButtonBorder,
        Color ButtonHover,
        Color InputBack,
        Color LogBack,
        Color LogText)
    {
        public Color SidebarGlossTop => Blend(Sidebar, Color.White, 0.16);
        public Color ButtonGlossTop => Blend(SecondaryButtonBack, Color.White, 0.24);
        public Color AccentGlossTop => Blend(Accent, Color.White, 0.30);
        public Color ProgressBack => Blend(InputBack, Color.Black, 0.10);

        public static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }

        public static InstallerPalette Resolve(SetupTheme theme, SetupWindow window)
        {
            string style = (window.Style ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(style) || style == "classic")
            {
                if (theme.Banner.Equals("minimal", StringComparison.OrdinalIgnoreCase) &&
                    theme.HeaderColor.Equals("gray", StringComparison.OrdinalIgnoreCase))
                    style = "dark";
                else if (theme.AccentColor.Equals("yellow", StringComparison.OrdinalIgnoreCase))
                    style = "amber";
                else if (theme.AccentColor.Equals("cyan", StringComparison.OrdinalIgnoreCase) &&
                    theme.ProgressColor.Equals("green", StringComparison.OrdinalIgnoreCase))
                    style = "oldschool";
                else
                    style = "blue";
            }

            return style switch
            {
                "dark" or "modern-dark" => new InstallerPalette(
                    Color.FromArgb(25, 29, 34),
                    Color.FromArgb(18, 22, 27),
                    Color.FromArgb(12, 17, 22),
                    Color.FromArgb(238, 242, 245),
                    Color.FromArgb(221, 226, 230),
                    Color.FromArgb(158, 170, 180),
                    Color.White,
                    Color.FromArgb(135, 152, 166),
                    ThemeColor(theme.AccentColor, Color.FromArgb(65, 199, 165)),
                    Color.FromArgb(74, 214, 178),
                    Color.FromArgb(42, 150, 125),
                    Color.Black,
                    Color.FromArgb(38, 45, 52),
                    Color.FromArgb(232, 236, 240),
                    Color.FromArgb(82, 94, 106),
                    Color.FromArgb(49, 58, 67),
                    Color.FromArgb(32, 37, 43),
                    Color.FromArgb(14, 17, 20),
                    Color.FromArgb(215, 225, 230)),
                "amber" or "terminal" => new InstallerPalette(
                    Color.FromArgb(20, 17, 10),
                    Color.FromArgb(31, 25, 12),
                    Color.FromArgb(48, 36, 11),
                    Color.FromArgb(255, 214, 122),
                    Color.FromArgb(248, 224, 172),
                    Color.FromArgb(195, 160, 92),
                    Color.FromArgb(255, 232, 166),
                    Color.FromArgb(210, 165, 83),
                    Color.FromArgb(222, 157, 42),
                    Color.FromArgb(241, 178, 60),
                    Color.FromArgb(169, 112, 22),
                    Color.Black,
                    Color.FromArgb(47, 37, 18),
                    Color.FromArgb(255, 224, 158),
                    Color.FromArgb(119, 86, 31),
                    Color.FromArgb(60, 47, 23),
                    Color.FromArgb(36, 29, 15),
                    Color.FromArgb(13, 11, 6),
                    Color.FromArgb(255, 218, 139)),
                "oldschool" or "retro" => new InstallerPalette(
                    Color.FromArgb(224, 224, 224),
                    Color.FromArgb(198, 198, 198),
                    Color.FromArgb(0, 0, 128),
                    Color.Black,
                    Color.Black,
                    Color.FromArgb(64, 64, 64),
                    Color.White,
                    Color.FromArgb(185, 210, 255),
                    Color.FromArgb(0, 128, 128),
                    Color.FromArgb(0, 150, 150),
                    Color.FromArgb(0, 96, 96),
                    Color.White,
                    Color.FromArgb(216, 216, 216),
                    Color.Black,
                    Color.FromArgb(96, 96, 96),
                    Color.FromArgb(232, 232, 232),
                    Color.White,
                    Color.White,
                    Color.Black),
                _ => new InstallerPalette(
                    Color.White,
                    Color.FromArgb(245, 245, 245),
                    Color.FromArgb(28, 54, 88),
                    Color.Black,
                    Color.FromArgb(32, 32, 32),
                    Color.FromArgb(70, 70, 70),
                    Color.White,
                    Color.FromArgb(185, 210, 230),
                    ThemeColor(theme.AccentColor, Color.FromArgb(0, 120, 215)),
                    Color.FromArgb(24, 140, 235),
                    Color.FromArgb(0, 90, 180),
                    Color.White,
                    Color.FromArgb(250, 250, 250),
                    Color.Black,
                    Color.FromArgb(170, 170, 170),
                    Color.FromArgb(235, 243, 252),
                    Color.White,
                    Color.White,
                    Color.Black)
            };
        }
    }

    private sealed class GlossyButton : Button
    {
        private InstallerPalette? _palette;
        private bool _secondary;

        public GlossyButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
        }

        public void SetPalette(InstallerPalette palette, bool secondary)
        {
            _palette = palette;
            _secondary = secondary;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_palette is null)
            {
                base.OnPaint(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = ClientRectangle;
            bounds.Width--;
            bounds.Height--;
            Color top = _secondary ? _palette.ButtonGlossTop : _palette.AccentGlossTop;
            Color bottom = _secondary ? _palette.SecondaryButtonBack : _palette.Accent;
            if (ClientRectangle.Contains(PointToClient(Cursor.Position)))
            {
                top = InstallerPalette.Blend(top, Color.White, 0.12);
                bottom = _secondary ? _palette.ButtonHover : _palette.AccentHover;
            }

            using var brush = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
            using var border = new Pen(_secondary ? _palette.ButtonBorder : _palette.AccentDown);
            using var shine = new SolidBrush(Color.FromArgb(80, Color.White));
            e.Graphics.FillRectangle(brush, bounds);
            e.Graphics.FillRectangle(shine, 1, 1, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height / 2 - 1));
            e.Graphics.DrawRectangle(border, bounds);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, _secondary ? _palette.ButtonText : _palette.AccentText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private sealed class GlossyProgressBar : ProgressBar
    {
        private InstallerPalette? _palette;

        public GlossyProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetPalette(InstallerPalette palette)
        {
            _palette = palette;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_palette is null)
            {
                base.OnPaint(e);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = ClientRectangle;
            bounds.Width--;
            bounds.Height--;
            using var back = new LinearGradientBrush(bounds, _palette.ProgressBack, _palette.InputBack, LinearGradientMode.Vertical);
            using var border = new Pen(_palette.ButtonBorder);
            e.Graphics.FillRectangle(back, bounds);
            e.Graphics.DrawRectangle(border, bounds);

            double percent = Maximum <= Minimum ? 1 : (Value - Minimum) / (double)(Maximum - Minimum);
            int fillWidth = Math.Clamp((int)Math.Round(bounds.Width * percent), 0, bounds.Width);
            if (fillWidth <= 0) return;

            var fill = new Rectangle(1, 1, fillWidth, Math.Max(1, bounds.Height - 1));
            using var fillBrush = new LinearGradientBrush(fill, _palette.AccentGlossTop, _palette.AccentDown, LinearGradientMode.Vertical);
            using var shine = new SolidBrush(Color.FromArgb(88, Color.White));
            e.Graphics.FillRectangle(fillBrush, fill);
            e.Graphics.FillRectangle(shine, fill.Left, fill.Top, fill.Width, Math.Max(1, fill.Height / 2));
        }
    }

    private sealed class InstallerMenuColors(InstallerPalette palette) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => palette.ContentBack;
        public override Color MenuItemSelected => palette.ButtonHover;
        public override Color MenuItemBorder => palette.Accent;
        public override Color ImageMarginGradientBegin => palette.ContentBack;
        public override Color ImageMarginGradientMiddle => palette.ContentBack;
        public override Color ImageMarginGradientEnd => palette.ContentBack;
        public override Color SeparatorDark => palette.ButtonBorder;
        public override Color SeparatorLight => palette.ButtonHover;
    }

    private sealed class AboutSetupForm : Form
    {
        public AboutSetupForm(PackageArchive archive, InstallerPalette palette)
        {
            Text = "About " + archive.Manifest.ProductName;
            Width = 440;
            Height = 270;
            MinimumSize = new Size(420, 250);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = palette.ContentBack;
            ForeColor = palette.Text;

            var header = new Panel { Dock = DockStyle.Top, Height = 82 };
            header.Paint += (_, e) =>
            {
                using var brush = new LinearGradientBrush(header.ClientRectangle, palette.SidebarGlossTop, palette.Sidebar, LinearGradientMode.Vertical);
                e.Graphics.FillRectangle(brush, header.ClientRectangle);
                using var shine = new SolidBrush(Color.FromArgb(45, Color.White));
                e.Graphics.FillRectangle(shine, 0, 0, header.Width, 35);
            };

            var title = new Label
            {
                Text = archive.Manifest.ProductName,
                Left = 18,
                Top = 15,
                Width = 380,
                Height = 26,
                Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
                BackColor = Color.Transparent,
                ForeColor = palette.SidebarTitle
            };
            var subtitle = new Label
            {
                Text = $"Version {archive.Manifest.Version} - {archive.Manifest.Publisher}",
                Left = 20,
                Top = 44,
                Width = 380,
                Height = 22,
                BackColor = Color.Transparent,
                ForeColor = palette.SidebarMuted
            };
            header.Controls.AddRange([title, subtitle]);

            var details = new Label
            {
                Text = $"{archive.Manifest.Description}{Environment.NewLine}{Environment.NewLine}Files: {archive.Entries.Count:N0}{Environment.NewLine}Package: {archive.CompressedBytes:N0} bytes{Environment.NewLine}Built with amSetup",
                Left = 20,
                Top = 100,
                Width = 390,
                Height = 96,
                ForeColor = palette.Text,
                BackColor = palette.ContentBack
            };

            var ok = new GlossyButton
            {
                Text = "OK",
                Width = 92,
                Height = 30,
                Left = Width - 128,
                Top = 200,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            ok.SetPalette(palette, secondary: false);
            ok.Click += (_, _) => Close();
            Controls.AddRange([header, details, ok]);
            AcceptButton = ok;
        }
    }

    private sealed class WinFormsInstallerUi(InstallerWizardForm form) : IInstallerUi
    {
        public void Header(string text) => Line(text);

        public void Line(string text)
        {
            if (form.IsDisposed) return;
            form.BeginInvoke(() =>
            {
                form._log.Items.Add(text);
                form._log.TopIndex = Math.Max(0, form._log.Items.Count - 1);
            });
        }

        public void Progress(int index, int count, long bytes, long total, string current, long currentBytes = 0, long currentTotal = 0)
        {
            if (form.IsDisposed) return;
            int percent = total <= 0 ? 100 : (int)Math.Clamp(bytes * 100 / total, 0, 100);
            int filePercent = currentTotal <= 0 ? 100 : (int)Math.Clamp(currentBytes * 100 / currentTotal, 0, 100);
            form.BeginInvoke(() =>
            {
                form._progress.Value = Math.Clamp(percent, 0, 100);
                form._progressText.Text = $"Overall: {percent}% - {index}/{count} files";
                form._fileProgress.Value = Math.Clamp(filePercent, 0, 100);
                form._fileProgressText.Text = $"Current file: {filePercent}% - {current}";
            });
        }

        public void ProgressClear()
        {
            if (form.IsDisposed) return;
            form.BeginInvoke(() =>
            {
                form._progress.Value = 100;
                form._fileProgress.Value = 100;
                form._progressText.Text = "Overall: 100%";
                form._fileProgressText.Text = "Finalizing...";
            });
        }
    }
}
#endif
