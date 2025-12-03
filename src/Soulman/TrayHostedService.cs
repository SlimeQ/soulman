using System.Diagnostics;
using System.Drawing;
using System.Threading;
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

    public TrayApplicationContext(IOptionsMonitor<SoulmanSettings> options, CloneFolderStore cloneStore,
        PathPreferenceStore pathStore, MoveNotificationBroker moveBroker, MoveLogStore moveLog, Icon? icon)
    {
        _options = options;
        _cloneStore = cloneStore;
        _pathStore = pathStore;
        _moveBroker = moveBroker;
        _moveLog = moveLog;
        _menu = new ContextMenuStrip();
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

        _menu.Items.Add(new ToolStripMenuItem("Soulman") { Enabled = false });
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
}
