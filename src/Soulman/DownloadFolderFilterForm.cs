#if WINDOWS
using System.Drawing;
using System.Windows.Forms;

namespace Soulman;

public sealed class DownloadFolderFilterForm : Form
{
    private readonly DownloadFilterManager _manager;
    private readonly ListBox _listBox;
    private readonly TextBox _pathInput;
    private readonly Button _addButton;
    private readonly Button _removeButton;
    private readonly Button _clearButton;

    public DownloadFolderFilterForm(DownloadFilterManager manager)
    {
        _manager = manager;

        Text = "Blocked Download Folders";
        Width = 540;
        Height = 440;
        MinimumSize = new Size(420, 320);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var helpLabel = new Label
        {
            Text = "Downloads under these sync-relative folders are skipped.\n" +
                   "Valid paths: Music/something, Movies/something, TV/something",
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(10),
            ForeColor = SystemColors.GrayText
        };

        var inputPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(10, 5, 10, 5)
        };

        _pathInput = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "e.g., Movies/Unsorted"
        };

        _addButton = new Button
        {
            Text = "Add",
            Dock = DockStyle.Right,
            Width = 84
        };
        _addButton.Click += OnAddClick;

        inputPanel.Controls.Add(_pathInput);
        inputPanel.Controls.Add(_addButton);

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
        _listBox.SelectedIndexChanged += (_, _) => UpdateButtons();
        listPanel.Controls.Add(_listBox);

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(10)
        };

        var flow = new FlowLayoutPanel
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

        flow.Controls.Add(closeButton);
        flow.Controls.Add(_clearButton);
        flow.Controls.Add(_removeButton);
        buttonPanel.Controls.Add(flow);

        Controls.Add(listPanel);
        Controls.Add(inputPanel);
        Controls.Add(helpLabel);
        Controls.Add(buttonPanel);

        AcceptButton = _addButton;
        CancelButton = closeButton;

        Load += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        _listBox.Items.Clear();
        foreach (var path in _manager.ListBlockedFolders())
        {
            _listBox.Items.Add(path);
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _removeButton.Enabled = _listBox.SelectedIndex >= 0;
        _clearButton.Enabled = _listBox.Items.Count > 0;
    }

    private void OnAddClick(object? sender, EventArgs e)
    {
        var path = _pathInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(
                "Please enter a folder block path.",
                "Soulman",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var (success, message) = _manager.AddFolderBlock(path);
        if (success)
        {
            _pathInput.Clear();
            RefreshList();
        }

        MessageBox.Show(
            message,
            success ? "Success" : "Error",
            MessageBoxButtons.OK,
            success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void OnRemoveClick(object? sender, EventArgs e)
    {
        if (_listBox.SelectedItems.Count == 0)
        {
            return;
        }

        var removed = 0;
        foreach (var item in _listBox.SelectedItems.Cast<object>().ToList())
        {
            var path = item.ToString();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var (success, _) = _manager.RemoveFolderBlock(path);
            if (success)
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            MessageBox.Show(
                $"Removed {removed} blocked folder{(removed == 1 ? string.Empty : "s")}.",
                "Success",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        RefreshList();
    }

    private void OnClearClick(object? sender, EventArgs e)
    {
        if (_listBox.Items.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Clear all {_listBox.Items.Count} blocked folders?",
            "Confirm Clear",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return;
        }

        var (success, message) = _manager.ClearFolderBlocks();
        MessageBox.Show(
            message,
            success ? "Success" : "Error",
            MessageBoxButtons.OK,
            success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        RefreshList();
    }
}
#endif
