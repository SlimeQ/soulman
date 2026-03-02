#if WINDOWS
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Soulman;

/// <summary>
/// Windows form for managing the PurgedPaths blacklist.
/// </summary>
public sealed class BlacklistForm : Form
{
    private readonly BlacklistManager _manager;
    private readonly ListBox _listBox;
    private readonly TextBox _pathInput;
    private readonly Button _addButton;
    private readonly Button _removeButton;
    private readonly Button _clearButton;
    private readonly Label _helpLabel;

    public BlacklistForm(BlacklistManager manager)
    {
        _manager = manager;

        Text = "Manage Blacklist";
        Width = 500;
        Height = 420;
        MinimumSize = new Size(400, 300);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        // Help label
        _helpLabel = new Label
        {
            Text = "Blacklisted paths are excluded from peer sync and auto-purged.\n" +
                   "Valid paths: Music/something, Movies/something, TV/something",
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(10),
            ForeColor = SystemColors.GrayText
        };

        // Path input
        var inputPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 5, 10, 5)
        };

        _pathInput = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "e.g., Music/Movies"
        };

        _addButton = new Button
        {
            Text = "Add",
            Dock = DockStyle.Right,
            Width = 80,
            Margin = new Padding(5, 0, 0, 0)
        };
        _addButton.Click += OnAddClick;

        inputPanel.Controls.Add(_pathInput);
        inputPanel.Controls.Add(_addButton);

        // List
        var listPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 0, 10, 10)
        };

        _listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            SelectionMode = SelectionMode.MultiExtended,
            Font = new Font(FontFamily.GenericMonospace, 10)
        };

        listPanel.Controls.Add(_listBox);

        // Button panel
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(10)
        };

        var flowPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };

        var closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Width = 90
        };

        _clearButton = new Button
        {
            Text = "Clear All",
            Width = 90,
            Margin = new Padding(0, 0, 10, 0)
        };
        _clearButton.Click += OnClearClick;

        _removeButton = new Button
        {
            Text = "Remove",
            Width = 90,
            Margin = new Padding(0, 0, 10, 0)
        };
        _removeButton.Click += OnRemoveClick;

        flowPanel.Controls.Add(closeButton);
        flowPanel.Controls.Add(_clearButton);
        flowPanel.Controls.Add(_removeButton);
        buttonPanel.Controls.Add(flowPanel);

        Controls.Add(listPanel);
        Controls.Add(inputPanel);
        Controls.Add(_helpLabel);
        Controls.Add(buttonPanel);

        AcceptButton = _addButton;
        CancelButton = closeButton;

        Load += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        _listBox.Items.Clear();
        var paths = _manager.List();
        foreach (var path in paths)
        {
            _listBox.Items.Add(path);
        }

        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        _removeButton.Enabled = _listBox.SelectedIndex >= 0;
        _clearButton.Enabled = _listBox.Items.Count > 0;
    }

    private void OnAddClick(object? sender, EventArgs e)
    {
        var path = _pathInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("Please enter a path to add.", "Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (success, message) = _manager.Add(path);
        
        if (success)
        {
            _pathInput.Clear();
            RefreshList();
        }
        
        MessageBox.Show(message, success ? "Success" : "Error",
            MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void OnRemoveClick(object? sender, EventArgs e)
    {
        if (_listBox.SelectedIndex < 0) return;

        var selected = _listBox.SelectedItems;
        var count = selected.Count;
        var removed = 0;

        foreach (var item in selected.Cast<object>().ToList())
        {
            var path = item.ToString();
            if (string.IsNullOrWhiteSpace(path)) continue;
            
            var (success, _) = _manager.Remove(path);
            if (success) removed++;
        }

        if (removed > 0)
        {
            MessageBox.Show($"Removed {removed} path{(removed == 1 ? "" : "s")} from blacklist.", 
                "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        RefreshList();
    }

    private void OnClearClick(object? sender, EventArgs e)
    {
        var count = _listBox.Items.Count;
        if (count == 0) return;

        var result = MessageBox.Show(
            $"Clear all {count} blacklisted paths?",
            "Confirm Clear",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes) return;

        var (success, message) = _manager.Clear();
        MessageBox.Show(message, success ? "Success" : "Error",
            MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        RefreshList();
    }
}
#endif