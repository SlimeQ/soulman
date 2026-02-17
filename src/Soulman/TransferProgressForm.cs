using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Soulman;

public class TransferProgressForm : Form
{
    private readonly TransferProgressBroker _broker;
    private readonly ListView _listView;
    private readonly Dictionary<string, ListViewItem> _items = new();
    private readonly System.Threading.Timer _cleanupTimer;

    public TransferProgressForm(TransferProgressBroker broker)
    {
        _broker = broker;

        Text = "Soulman Transfers";
        Size = new Size(600, 400);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };

        _listView.Columns.Add("File", 350);
        _listView.Columns.Add("Progress", 100);
        _listView.Columns.Add("Size", 100);

        Controls.Add(_listView);

        _broker.ProgressChanged += OnProgressChanged;

        // Cleanup finished items every 5 seconds
        _cleanupTimer = new System.Threading.Timer(CleanupFinished, null, 5000, 5000);

        FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void OnProgressChanged(object? sender, TransferProgress e)
    {
        if (IsDisposed || !IsHandleCreated) return;

        Invoke(new Action(() =>
        {
            if (!_items.TryGetValue(e.FileName, out var item))
            {
                item = new ListViewItem(e.FileName);
                item.SubItems.Add("");
                item.SubItems.Add(FormatSize(e.TotalBytes));
                _listView.Items.Insert(0, item);
                _items[e.FileName] = item;
            }

            if (e.IsComplete)
            {
                item.SubItems[1].Text = "Complete";
                item.ForeColor = Color.Gray;
                item.Tag = DateTime.Now; // Mark for cleanup
            }
            else
            {
                item.SubItems[1].Text = $"{e.Percentage:F1}%";
            }
        }));
    }

    private void CleanupFinished(object? state)
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            Invoke(new Action(() =>
            {
                var cutoff = DateTime.Now.AddSeconds(-10);
                var toRemove = new List<ListViewItem>();

                foreach (ListViewItem item in _listView.Items)
                {
                    if (item.Tag is DateTime completedAt && completedAt < cutoff)
                    {
                        toRemove.Add(item);
                    }
                }

                foreach (var item in toRemove)
                {
                    _listView.Items.Remove(item);
                    _items.Remove(item.Text);
                }
            }));
        }
        catch
        {
            // Ignore UI races
        }
    }

    private string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
             _broker.ProgressChanged -= OnProgressChanged;
             _cleanupTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
