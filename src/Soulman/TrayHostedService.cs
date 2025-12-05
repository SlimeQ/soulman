using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;
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
    private Thread? _uiThread;
    private TrayApplicationContext? _context;
    private readonly ManualResetEventSlim _started = new(false);

    public TrayHostedService(
        ILogger<TrayHostedService> logger,
        IOptionsMonitor<SoulmanSettings> options,
        CloneFolderStore cloneStore,
        PathPreferenceStore pathStore,
        MoveNotificationBroker moveBroker,
        MoveLogStore moveLog)
    {
        _logger = logger;
        _options = options;
        _cloneStore = cloneStore;
        _pathStore = pathStore;
        _moveBroker = moveBroker;
        _moveLog = moveLog;
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

            _context = new TrayApplicationContext(_options, _cloneStore, _pathStore, _moveBroker, _moveLog, icon);
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
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private readonly CloneFolderStore _cloneStore;
    private readonly PathPreferenceStore _pathStore;
    private readonly MoveNotificationBroker _moveBroker;
    private readonly MoveLogStore _moveLog;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly string _startupShortcutPath;
    private readonly SynchronizationContext? _uiContext;

    public TrayApplicationContext(IOptionsMonitor<SoulmanSettings> options, CloneFolderStore cloneStore,
        PathPreferenceStore pathStore, MoveNotificationBroker moveBroker, MoveLogStore moveLog, Icon? icon)
    {
        _options = options;
        _cloneStore = cloneStore;
        _pathStore = pathStore;
        _moveBroker = moveBroker;
        _moveLog = moveLog;
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
        base.ExitThreadCore();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var settings = _options.CurrentValue;
        var prefs = _pathStore.Get();
        var sourcePath = prefs.SourcePath ?? settings.SourcePath;
        var destPath = prefs.DestinationPath ?? settings.DestinationPath;

        _menu.Items.Add(new ToolStripMenuItem(GetVersionLabel()) { Enabled = false });
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

    private static string GetVersionLabel()
    {
        try
        {
            var releaseTag = TryGetAssemblyMetadata("ReleaseTag");
            if (!string.IsNullOrWhiteSpace(releaseTag))
            {
                return $"Soulman {releaseTag}";
            }

            var clickOnceVersion = TryGetClickOnceVersion();
            if (!string.IsNullOrWhiteSpace(clickOnceVersion))
            {
                return $"Soulman {clickOnceVersion}";
            }

            var productVersion = TryGetProductVersion();
            if (!string.IsNullOrWhiteSpace(productVersion))
            {
                return $"Soulman {productVersion}";
            }

            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(info))
            {
                return $"Soulman {TrimMetadata(info)}";
            }

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            if (ver != null)
            {
                return $"Soulman {TrimMetadata(ver.ToString())}";
            }
        }
        catch
        {
            // ignore and fallback
        }

        return "Soulman";
    }

    private static string? TryGetAssemblyMetadata(string key)
    {
        try
        {
            var attrs = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return attrs.FirstOrDefault()?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProductVersion()
    {
        try
        {
            var path = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var ver = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return TrimMetadata(ver);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetClickOnceVersion()
    {
        try
        {
            var deploymentAsm = Assembly.Load("System.Deployment, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
            var type = deploymentAsm?.GetType("System.Deployment.Application.ApplicationDeployment");
            if (type == null)
            {
                return null;
            }

            var isNetworkDeployedProp = type.GetProperty("IsNetworkDeployed", BindingFlags.Public | BindingFlags.Static);
            if (isNetworkDeployedProp == null)
            {
                return null;
            }

            var isNetworkDeployed = isNetworkDeployedProp.GetValue(null) as bool?;
            if (isNetworkDeployed != true)
            {
                return null;
            }

            var currentDeploymentProp = type.GetProperty("CurrentDeployment", BindingFlags.Public | BindingFlags.Static);
            var currentDeployment = currentDeploymentProp?.GetValue(null);
            if (currentDeployment == null)
            {
                return null;
            }

            var versionProp = currentDeployment.GetType().GetProperty("CurrentVersion");
            var versionValue = versionProp?.GetValue(currentDeployment)?.ToString();
            return versionValue;
        }
        catch
        {
            return null;
        }
    }

    private static string TrimMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var core = value.Split('+')[0];
        return core;
    }
}
