using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Soulman;

public class MoveLogForm : Form
{
    private readonly MoveLogStore _logStore;
    private readonly DataGridView _grid;
    private readonly Button _refresh;

    public MoveLogForm(MoveLogStore logStore)
    {
        _logStore = logStore;
        Text = "Soulman – Recent Moves";
        Width = 900;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", DataPropertyName = "Time", FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source", DataPropertyName = "Source", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Destination", DataPropertyName = "Destination", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Clones", DataPropertyName = "Clones", FillWeight = 20 });

        _refresh = new Button
        {
            Text = "Refresh",
            Dock = DockStyle.Top,
            Height = 32
        };
        _refresh.Click += (_, _) => LoadData();

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        panel.Controls.Add(_grid);
        panel.Controls.Add(_refresh);

        Controls.Add(panel);

        Load += (_, _) => LoadData();
    }

    private void LoadData()
    {
        var entries = _logStore.GetRecentEntries()
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new
            {
                Time = e.Timestamp.ToLocalTime().ToString("g"),
                Source = e.SourcePath,
                Destination = e.DestinationPath,
                Clones = (e.CloneDestinations?.Any() ?? false)
                    ? string.Join(", ", e.CloneDestinations)
                    : "—"
            })
            .ToList();

        _grid.DataSource = entries;
    }
}
