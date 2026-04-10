using xProtoView.Services;

namespace xProtoView;

public sealed class SettingsDialog : Form
{
    private readonly AppConfig _editingConfig;
    private readonly Action<WindowLayoutConfig> _onLayoutChanged;
    private readonly Label _lblConfigPath = new() { AutoSize = true };
    private readonly ListBox _lstPaths = new() { Dock = DockStyle.Fill };

    public AppConfig? UpdatedConfig { get; private set; }

    public SettingsDialog(string configPath, AppConfig config, Action<WindowLayoutConfig> onLayoutChanged)
    {
        _editingConfig = config;
        _onLayoutChanged = onLayoutChanged;
        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 560);
        Size = new Size(900, 620);

        BuildUi(configPath);
        RenderSettings();
        // 打开时恢复设置窗口布局。
        WindowLayoutHelper.ApplyLayout(this, _editingConfig.Ui.SettingsDialog);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // 关闭时回传设置窗口布局。
        _onLayoutChanged(WindowLayoutHelper.CaptureLayout(this));
    }

    private void BuildUi(string configPath)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lblConfigPath.Text = $"配置路径：{configPath}";
        root.Controls.Add(_lblConfigPath, 0, 0);

        root.Controls.Add(new Label { Text = "路径（可添加目录或 .proto 文件）", AutoSize = true }, 0, 1);
        root.Controls.Add(_lstPaths, 0, 2);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };

        var btnCancel = new Button { Text = "取消", AutoSize = true };
        btnCancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        var btnSave = new Button { Text = "保存并关闭", AutoSize = true };
        btnSave.Click += (_, _) => SaveAndClose();
        var btnRemove = new Button { Text = "移除路径", AutoSize = true };
        btnRemove.Click += (_, _) => RemoveSelected(_lstPaths, _editingConfig.Proto.PathEntries);
        var btnAddFile = new Button { Text = "添加文件...", AutoSize = true };
        btnAddFile.Click += (_, _) => AddFiles();
        var btnAddDir = new Button { Text = "添加目录...", AutoSize = true };
        btnAddDir.Click += (_, _) => AddDirectory();

        btnRow.Controls.AddRange([btnCancel, btnSave, btnRemove, btnAddFile, btnAddDir]);
        root.Controls.Add(btnRow, 0, 3);

        Controls.Add(root);
    }

    private void RenderSettings()
    {
        _lstPaths.Items.Clear();
        foreach (var item in _editingConfig.Proto.PathEntries)
        {
            _lstPaths.Items.Add(item.DisplayText);
        }
    }

    private void AddDirectory()
    {
        using var dlg = new FolderBrowserDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            var includeSub = MessageBox.Show(
                this,
                "是否包含子目录中的 .proto 文件？",
                "添加目录",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) == DialogResult.Yes;
            _editingConfig.Proto.PathEntries.Add(new ProtoPathEntry
            {
                Path = dlg.SelectedPath,
                IsDirectory = true,
                IncludeSubDirectories = includeSub
            });
            RenderSettings();
        }
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Proto files (*.proto)|*.proto",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _editingConfig.Proto.PathEntries.AddRange(dlg.FileNames.Select(x => new ProtoPathEntry
            {
                Path = x,
                IsDirectory = false,
                IncludeSubDirectories = false
            }));
            RenderSettings();
        }
    }

    private void SaveAndClose()
    {
        _editingConfig.Proto.PathEntries = _editingConfig.Proto.PathEntries
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => new ProtoPathEntry
            {
                Path = x.Path.Trim(),
                IsDirectory = x.IsDirectory,
                IncludeSubDirectories = x.IsDirectory && x.IncludeSubDirectories
            })
            .DistinctBy(x => $"{x.Path}|{x.IsDirectory}|{x.IncludeSubDirectories}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        _editingConfig.Proto.Paths = _editingConfig.Proto.PathEntries
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        UpdatedConfig = _editingConfig;
        DialogResult = DialogResult.OK;
    }

    private static void RemoveSelected(ListBox list, List<ProtoPathEntry> target)
    {
        var selectedIndex = list.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= target.Count)
        {
            return;
        }

        target.RemoveAt(selectedIndex);
        list.Items.RemoveAt(selectedIndex);
    }
}
