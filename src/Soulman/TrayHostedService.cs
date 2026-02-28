using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Options;

namespace Soulman;

public class TrayHostedService : IHostedService, IDisposable
{
    private readonly ILogger<TrayHostedService> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private readonly CloneFolderStore _cloneStore;
    private readonly PathPreferenceStore _pathStore;
    private readonly MoveNotificationBroker _moveBroker;
    private readonly MoveLogStore _moveLog;
    private readonly InstanceDiscovery _discovery;
    private readonly TransferProgressBroker _progressBroker;
    private Thread? _uiThread;
    private TrayApplicationContext? _context;
    private readonly ManualResetEventSlim _started = new(false);

    public TrayHostedService(
        ILogger<TrayHostedService> logger,
        IOptionsMonitor<SoulmanSettings> options,
        CloneFolderStore cloneStore,
        PathPreferenceStore pathStore,
        MoveNotificationBroker moveBroker,
        MoveLogStore moveLog,
        InstanceDiscovery discovery,
        TransferProgressBroker progressBroker)
    {
        _logger = logger;
        _options = options;
        _cloneStore = cloneStore;
        _pathStore = pathStore;
        _moveBroker = moveBroker;
        _moveLog = moveLog;
        _discovery = discovery;
        _progressBroker = progressBroker;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _uiThread = new Thread(RunTray)
        {
            IsBackground = true,
            Name = "Soulman Tray"
        };
        _uiThread.TrySetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _started.Wait(cancellationToken);
        return Task.CompletedTask;
    }

    private void RunTray()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "soulman.ico");
            Icon? icon = null;
            if (File.Exists(iconPath))
            {
                icon = new Icon(iconPath);
            }
            else
            {
                icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }

            _context = new TrayApplicationContext(_logger, _options, _cloneStore, _pathStore, _moveBroker, _moveLog,
                _discovery, _progressBroker, icon);
            _started.Set();
            Application.Run(_context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray thread crashed");
        }
        finally
        {
            _started.Set();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_context != null)
        {
            _moveBroker.Unsubscribe(_context.OnMove);
            _context.ExitThread();
        }

        if (_uiThread != null && _uiThread.IsAlive)
        {
            _uiThread.Join(TimeSpan.FromSeconds(5));
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _context?.Dispose();
    }
}

internal class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan InstanceDiscoveryTimeout = TimeSpan.FromSeconds(2);
    private readonly ILogger<TrayHostedService> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private readonly CloneFolderStore _cloneStore;
    private readonly PathPreferenceStore _pathStore;
    private readonly MoveNotificationBroker _moveBroker;
    private readonly MoveLogStore _moveLog;
    private readonly InstanceDiscovery _discovery;
    private readonly TransferProgressBroker _progressBroker;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly string _startupShortcutPath;
    private readonly SynchronizationContext? _uiContext;
    private ToolStripMenuItem? _instancesMenu;
    private int _instanceRefreshInFlight;
    private TransferProgressForm? _progressForm;

    public TrayApplicationContext(ILogger<TrayHostedService> logger, IOptionsMonitor<SoulmanSettings> options,
        CloneFolderStore cloneStore,
        PathPreferenceStore pathStore, MoveNotificationBroker moveBroker, MoveLogStore moveLog,
        InstanceDiscovery discovery, TransferProgressBroker progressBroker, Icon? icon)
    {
        _logger = logger;
        _options = options;
        _cloneStore = cloneStore;
        _pathStore = pathStore;
        _moveBroker = moveBroker;
        _moveLog = moveLog;
        _discovery = discovery;
        _progressBroker = progressBroker;
        _menu = new ContextMenuStrip();
        _uiContext = SynchronizationContext.Current;
        _startupShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Soulman.lnk");
        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "Soulman"
        };

        BuildMenu();
        _moveBroker.Subscribe(OnMove);
    }

    protected override void ExitThreadCore()
    {
        _moveBroker.Unsubscribe(OnMove);
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _progressForm?.Dispose();
        base.ExitThreadCore();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var settings = _options.CurrentValue;
        var prefs = _pathStore.Get();
        var sourcePath = prefs.SourcePath ?? settings.SourcePath;
        var destPath = prefs.DestinationPath ?? settings.DestinationPath;

        _menu.Items.Add(new ToolStripMenuItem(SoulmanVersion.GetLabel()) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        _instancesMenu = new ToolStripMenuItem("Other Soulman Instances");
        _instancesMenu.DropDownItems.Add(new ToolStripMenuItem("Searching...") { Enabled = false });
        _instancesMenu.DropDownOpening += async (_, _) => await RefreshInstancesAsync();
        _menu.Items.Add(_instancesMenu);
        _menu.Items.Add(new ToolStripSeparator());

        var addItem = new ToolStripMenuItem("Add Clone Destination...");
        addItem.Click += (_, _) => AddCloneFolder();
        _menu.Items.Add(addItem);

        var clones = _cloneStore.GetFolders();
        if (clones.Count > 0)
        {
            var cloneMenu = new ToolStripMenuItem("Clone Destinations");
            foreach (var folder in clones)
            {
                var item = new ToolStripMenuItem(folder);
                item.Click += (_, _) => OpenFolder(folder);
                cloneMenu.DropDownItems.Add(item);
            }
            _menu.Items.Add(cloneMenu);

            var clearItem = new ToolStripMenuItem("Clear Clone Destinations");
            clearItem.Click += (_, _) => { _cloneStore.Clear(); BuildMenu(); };
            _menu.Items.Add(clearItem);
        }

        _menu.Items.Add(new ToolStripSeparator());

        var setSource = new ToolStripMenuItem($"Set Source Folder...{DisplayPathSuffix(sourcePath)}");
        setSource.Click += (_, _) => SetSourceFolder();
        _menu.Items.Add(setSource);

        var openSettings = new ToolStripMenuItem("Settings...");
        openSettings.Click += (_, _) => OpenSettingsPanel();
        _menu.Items.Add(openSettings);

        var setDest = new ToolStripMenuItem($"Set Destination Folder...{DisplayPathSuffix(destPath)}");
        setDest.Click += (_, _) => SetDestinationFolder();
        _menu.Items.Add(setDest);

        var openSource = new ToolStripMenuItem("Open Source Folder");
        openSource.Click += (_, _) => OpenFolder(sourcePath);
        _menu.Items.Add(openSource);

        var openDest = new ToolStripMenuItem("Open Destination Folder");
        openDest.Click += (_, _) => OpenFolder(destPath);
        _menu.Items.Add(openDest);

        var openLog = new ToolStripMenuItem("Open Move Log");
        openLog.Click += (_, _) => OpenMoveLog();
        _menu.Items.Add(openLog);

        var openAppLogs = new ToolStripMenuItem("Open App Logs");
        openAppLogs.Click += (_, _) => OpenAppLogs();
        _menu.Items.Add(openAppLogs);

        var openTransfers = new ToolStripMenuItem("Open Transfers");
        openTransfers.Click += (_, _) => OpenTransfers();
        _menu.Items.Add(openTransfers);

        _menu.Items.Add(new ToolStripSeparator());

        var update = new ToolStripMenuItem("Update to Latest");
        update.Click += (_, _) => Task.Run(UpdateToLatestAsync);
        _menu.Items.Add(update);

        var startupItem = new ToolStripMenuItem("Run on Startup")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = false
        };
        startupItem.Click += (_, _) => ToggleStartup(startupItem);
        _menu.Items.Add(startupItem);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        _menu.Items.Add(exit);

        _notifyIcon.ContextMenuStrip = _menu;
    }

    private async Task RefreshInstancesAsync()
    {
        if (_instancesMenu == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _instanceRefreshInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            PostToUi(() => SetInstanceMenuItems(new List<ToolStripItem>
            {
                new ToolStripMenuItem("Searching...") { Enabled = false }
            }));

            IReadOnlyCollection<DiscoveredInstance> instances;
            try
            {
                instances = await _discovery.DiscoverAsync(InstanceDiscoveryTimeout, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to discover other Soulman instances");
                PostToUi(() => SetInstanceMenuItems(new List<ToolStripItem>
                {
                    new ToolStripMenuItem("Discovery failed; try refresh") { Enabled = false }
                }));
                return;
            }

            var items = instances
                .OrderBy(i => i.MachineName, StringComparer.OrdinalIgnoreCase)
                .Select(instance =>
                {
                    var label = BuildInstanceLabel(instance);
                    var item = new ToolStripMenuItem(label) { Enabled = false };
                    item.ToolTipText = instance.EndPoint.ToString();
                    return item;
                })
                .ToList();

            if (items.Count == 0)
            {
                items.Add(new ToolStripMenuItem("No other instances found") { Enabled = false });
            }

            PostToUi(() => SetInstanceMenuItems(items));
        }
        finally
        {
            Interlocked.Exchange(ref _instanceRefreshInFlight, 0);
        }
    }

    private void SetInstanceMenuItems(IReadOnlyCollection<ToolStripItem> items)
    {
        if (_instancesMenu == null)
        {
            return;
        }

        _instancesMenu.DropDownItems.Clear();
        foreach (var item in items)
        {
            _instancesMenu.DropDownItems.Add(item);
        }

        _instancesMenu.DropDownItems.Add(new ToolStripSeparator());
        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += async (_, _) => await RefreshInstancesAsync();
        _instancesMenu.DropDownItems.Add(refresh);
    }

    private static string BuildInstanceLabel(DiscoveredInstance instance)
    {
        var versionSuffix = string.IsNullOrWhiteSpace(instance.Version) ? string.Empty : $" {instance.Version}";
        return $"Soulman{versionSuffix} on {instance.MachineName}";
    }

    private void SetSourceFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the primary source folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _pathStore.SetSource(dialog.SelectedPath);
            BuildMenu();
        }
    }

    private void SetDestinationFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the destination music folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _pathStore.SetDestination(dialog.SelectedPath);
            BuildMenu();
        }
    }

    private void OpenSettingsPanel()
    {
        try
        {
            var configPath = GetConfigPath();
            var current = SoulmanTraySettings.FromCurrent(_options.CurrentValue, _pathStore.Get());

            using var form = new SoulmanSettingsForm(current);
            if (form.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            var updated = form.GetSettings();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var payload = JsonSerializer.Serialize(new { Soulman = updated }, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(configPath, payload);

            _notifyIcon.ShowBalloonTip(
                3000,
                "Soulman",
                $"Settings saved to {configPath}. Restarting Soulman to apply changes...",
                ToolTipIcon.Info);

            RestartApplication();
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(
                4000,
                "Soulman",
                $"Failed to save settings: {ex.Message}",
                ToolTipIcon.Warning);
        }
    }

    private static string GetConfigPath()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Soulman");
        return Path.Combine(configDir, "appsettings.json");
    }

    public void OnMove(int count, string destination)
    {
        if (count <= 0)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(
            4000,
            "Soulman",
            $"Moved {count} file{(count == 1 ? string.Empty : "s")} to {destination}",
            ToolTipIcon.Info);
    }

    private void OpenMoveLog()
    {
        try
        {
            using var form = new MoveLogForm(_moveLog);
            form.ShowDialog();
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "Soulman",
                $"Could not open move log: {ex.Message}",
                ToolTipIcon.Warning);
        }
    }

    private void OpenAppLogs()
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulman", "logs");
            if (!Directory.Exists(logDir))
            {
                _notifyIcon.ShowBalloonTip(3000, "Soulman", "No logs found yet.", ToolTipIcon.Info);
                return;
            }

            var latest = Directory.GetFiles(logDir, "soulman-*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            if (latest == null)
            {
                _notifyIcon.ShowBalloonTip(3000, "Soulman", "No logs found yet.", ToolTipIcon.Info);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = latest,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(3000, "Soulman", $"Failed to open logs: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void OpenTransfers()
    {
        if (_progressForm == null || _progressForm.IsDisposed)
        {
            _progressForm = new TransferProgressForm(_progressBroker);
        }

        if (!_progressForm.Visible)
        {
            _progressForm.Show();
        }
        else
        {
            _progressForm.Activate();
        }
    }

    private static string DisplayPathSuffix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? string.Empty : $" ({name})";
    }

    private void AddCloneFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a clone folder (network locations supported)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            if (_cloneStore.AddFolder(dialog.SelectedPath))
            {
                BuildMenu();
            }
            else
            {
                _notifyIcon.ShowBalloonTip(
                    3000,
                    "Soulman",
                    "Folder already added or invalid.",
                    ToolTipIcon.Info);
            }
        }
    }

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{full}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // swallow UI errors
        }
    }

    private bool IsStartupEnabled()
    {
        return File.Exists(_startupShortcutPath);
    }

    private void ToggleStartup(ToolStripMenuItem item)
    {
        if (IsStartupEnabled())
        {
            if (DisableStartup())
            {
                item.Checked = false;
                _notifyIcon.ShowBalloonTip(2000, "Soulman", "Startup launch disabled", ToolTipIcon.Info);
            }
        }
        else
        {
            if (EnableStartup())
            {
                item.Checked = true;
                _notifyIcon.ShowBalloonTip(2000, "Soulman", "Startup launch enabled", ToolTipIcon.Info);
            }
        }
    }

    private bool EnableStartup()
    {
        try
        {
            var target = Application.ExecutablePath;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_startupShortcutPath)!);
            dynamic? shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            if (shell == null)
            {
                return false;
            }

            dynamic shortcut = shell.CreateShortcut(_startupShortcutPath);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.Description = "Soulman music mover";
            shortcut.IconLocation = target;
            shortcut.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool DisableStartup()
    {
        try
        {
            if (File.Exists(_startupShortcutPath))
            {
                File.Delete(_startupShortcutPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task UpdateToLatestAsync()
    {
        Notify("Soulman", "Checking for updates...", ToolTipIcon.Info);
        try
        {
            var apiUrl = "https://api.github.com/repos/SlimeQ/soulman/releases/latest";
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Soulman-Tray/1.0");

            using var json = JsonDocument.Parse(await client.GetStringAsync(apiUrl));
            var assets = json.RootElement.GetProperty("assets");
            string? downloadUrl = null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name) &&
                    name.EndsWith("soulman_installer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            downloadUrl ??= assets.EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u) &&
                                     u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidOperationException("No installer asset found on the latest release.");
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "soulman_installer.exe");
            await using (var target = System.IO.File.Create(tempPath))
            await using (var stream = await client.GetStreamAsync(downloadUrl))
            {
                await stream.CopyToAsync(target);
            }

            Notify("Soulman", "Launching installer...", ToolTipIcon.Info);
            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Notify("Soulman", $"Update failed: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void RestartApplication()
    {
        try
        {
            var exe = Application.ExecutablePath;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to relaunch Soulman after settings save.");
        }

        ExitThread();
        Environment.Exit(0);
    }

    private void PostToUi(Action action)
    {
        if (_uiContext != null)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private void Notify(string title, string message, ToolTipIcon icon)
    {
        PostToUi(() => _notifyIcon.ShowBalloonTip(3000, title, message, icon));
    }
}

internal sealed class SoulmanSettingsForm : Form
{
    private readonly TextBox _sourcePath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _destinationPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _moviePath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _tvPath = new() { Dock = DockStyle.Fill };
    private readonly string? _initialSyncRootPath;
    private readonly NumericUpDown _pollSeconds = new() { Minimum = 5, Maximum = 3600, Dock = DockStyle.Fill };
    private readonly NumericUpDown _settledSeconds = new() { Minimum = 5, Maximum = 3600, Dock = DockStyle.Fill };
    private readonly TextBox _additionalSources = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

    public SoulmanSettingsForm(SoulmanTraySettings settings)
    {
        Text = "Soulman Settings";
        Width = 760;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;

        _sourcePath.Text = settings.SourcePath ?? string.Empty;
        _destinationPath.Text = settings.DestinationPath ?? string.Empty;
        _moviePath.Text = settings.MovieDestinationPath ?? string.Empty;
        _tvPath.Text = settings.TvDestinationPath ?? string.Empty;
        _initialSyncRootPath = settings.SyncRootPath;
        _pollSeconds.Value = Math.Clamp(settings.PollIntervalSeconds, 5, 3600);
        _settledSeconds.Value = Math.Clamp(settings.SettledSeconds, 5, 3600);
        _additionalSources.Text = string.Join(Environment.NewLine, settings.AdditionalSources ?? new List<string>());

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(10),
            AutoSize = true
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(table, 0, "SourcePath", _sourcePath);
        AddRow(table, 1, "DestinationPath (Music)", _destinationPath);
        AddRow(table, 2, "MovieDestinationPath", _moviePath);
        AddRow(table, 3, "TvDestinationPath", _tvPath);
        AddRow(table, 4, "PollIntervalSeconds", _pollSeconds);
        AddRow(table, 5, "SettledSeconds", _settledSeconds);
        AddRow(table, 6, "AdditionalSources (one per line)", _additionalSources, 140);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            Height = 52
        };

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        buttonPanel.Controls.Add(save);
        buttonPanel.Controls.Add(cancel);

        Controls.Add(table);
        Controls.Add(buttonPanel);

        AcceptButton = save;
        CancelButton = cancel;
    }

    public SoulmanTraySettings GetSettings()
    {
        return new SoulmanTraySettings
        {
            SourcePath = NullIfEmpty(_sourcePath.Text),
            DestinationPath = NullIfEmpty(_destinationPath.Text),
            MovieDestinationPath = NullIfEmpty(_moviePath.Text),
            TvDestinationPath = NullIfEmpty(_tvPath.Text),
            SyncRootPath = _initialSyncRootPath,
            PollIntervalSeconds = (int)_pollSeconds.Value,
            SettledSeconds = (int)_settledSeconds.Value,
            AdditionalSources = _additionalSources.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static void AddRow(TableLayoutPanel table, int rowIndex, string label, Control control, int minHeight = 30)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0)
        };
        table.Controls.Add(lbl, 0, rowIndex);
        control.MinimumSize = new Size(120, minHeight);
        table.Controls.Add(control, 1, rowIndex);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class SoulmanTraySettings
{
    public string? SourcePath { get; set; }
    public string? DestinationPath { get; set; }
    public string? MovieDestinationPath { get; set; }
    public string? TvDestinationPath { get; set; }
    public string? SyncRootPath { get; set; }
    public List<string> AdditionalSources { get; set; } = new();
    public int PollIntervalSeconds { get; set; } = 30;
    public int SettledSeconds { get; set; } = 20;

    public static SoulmanTraySettings FromCurrent(SoulmanSettings current, PathPreferences prefs)
    {
        return new SoulmanTraySettings
        {
            SourcePath = prefs.SourcePath ?? current.SourcePath,
            DestinationPath = prefs.DestinationPath ?? current.DestinationPath,
            MovieDestinationPath = current.MovieDestinationPath,
            TvDestinationPath = current.TvDestinationPath,
            SyncRootPath = current.SyncRootPath,
            AdditionalSources = current.AdditionalSources?.ToList() ?? new List<string>(),
            PollIntervalSeconds = current.PollIntervalSeconds,
            SettledSeconds = current.SettledSeconds
        };
    }
}
