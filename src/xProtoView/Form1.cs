using xProtoView.Services;

namespace xProtoView;

public partial class Form1 : Form
{
    private readonly ConfigService _configService = new();
    private readonly ProtoFileService _protoFileService = new();
    private readonly ProtocDecoder _decoder = new();
    private readonly ProtoTextYamlConverter _yamlConverter = new();

    private AppConfig _config = AppConfig.Default();
    private List<string> _protoFiles = [];
    private List<string> _messageTypes = [];
    private bool _isFilteringMessageType;

    private readonly MenuStrip _menuStrip = new();
    private readonly ToolStripMenuItem _menuSettings = new("设置");
    // 底部状态栏统一承载提示信息。
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    // Base64 文本框默认自动换行，便于查看长内容。
    private readonly TextBox _txtBase64 = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Height = 220, Dock = DockStyle.Fill };
    private readonly ComboBox _cmbMessageType = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _txtProto = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, ReadOnly = false, Dock = DockStyle.Fill };

    public Form1()
    {
        InitializeComponent();
        BuildUi();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // 启动时先恢复主窗口布局。
        _config = _configService.ReadConfig();
        WindowLayoutHelper.ApplyLayout(this, _config.Ui.MainWindow);
        await ReloadConfigAndProtosAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        try
        {
            // 关闭时保存主窗口布局。
            _config.Ui.MainWindow = WindowLayoutHelper.CaptureLayout(this);
            _configService.WriteConfig(_config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存主窗口布局失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BuildUi()
    {
        Text = "xProtoView (C#)";
        MinimumSize = new Size(1100, 760);
        ClientSize = new Size(1300, 860);
        // 顶部菜单。
        BuildMenu();
        BuildStatusBar();
        // 主体解析视图。
        Controls.Add(_menuStrip);
        Controls.Add(_statusStrip);
        Controls.Add(BuildParseView());
        MainMenuStrip = _menuStrip;
    }

    private void BuildMenu()
    {
        _menuSettings.Click += async (_, _) => await OpenSettingsDialogAsync();
        _menuStrip.Items.Add(_menuSettings);
    }

    private void BuildStatusBar()
    {
        // 状态栏显示最新提示，避免占用操作区空间。
        _statusStrip.Items.Add(_statusLabel);
    }

    private Control BuildParseView()
    {
        const int topPanelMinSize = 120;
        const int bottomPanelMinSize = 220;
        const int preferredSplitDistance = 260;
        // 使用上下分割容器，允许直接拖动 Base64 下边界调整高度。
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            // 先使用 0 避免初始化阶段触发最小尺寸越界检查。
            Panel1MinSize = 0,
            Panel2MinSize = 0
        };
        // 在实际尺寸就绪后统一应用最小尺寸与分割位置，避免越界异常。
        split.Layout += (_, _) => EnsureSplitConstraintsInRange(split, topPanelMinSize, bottomPanelMinSize, preferredSplitDistance);

        // 上半区仅包含 Base64 标题与输入框。
        var base64Panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        base64Panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        base64Panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        base64Panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        base64Panel.Controls.Add(new Label { Text = "Base64", AutoSize = true }, 0, 0);
        base64Panel.Controls.Add(_txtBase64, 0, 1);
        split.Panel1.Controls.Add(base64Panel);

        // Message 类型和操作按钮放同一行，减少垂直占用。
        var actionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actionRow.Controls.Add(new Label { Text = "Message 类型（必填）", AutoSize = true, Padding = new Padding(0, 8, 8, 0) }, 0, 0);
        _cmbMessageType.Dock = DockStyle.Fill;
        // 输入时实时过滤 message 类型，便于快速定位。
        _cmbMessageType.TextUpdate += (_, _) => ApplyMessageTypeFilter(_cmbMessageType.Text);
        // 展开下拉时按当前输入文本同步过滤结果。
        _cmbMessageType.DropDown += (_, _) => ApplyMessageTypeFilter(_cmbMessageType.Text);
        actionRow.Controls.Add(_cmbMessageType, 1, 0);

        // 操作按钮固定为单行布局，避免窗口缩放时被裁剪隐藏。
        var btnRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(8, 0, 0, 0)
        };
        var btnDecode = new Button { Text = "base64->proto", AutoSize = true };
        btnDecode.Click += async (_, _) => await DecodeAsync();
        btnRow.Controls.Add(btnDecode);
        var btnEncode = new Button { Text = "proto->base64", AutoSize = true };
        btnEncode.Click += async (_, _) => await EncodeAsync();
        btnRow.Controls.Add(btnEncode);
        var btnYamlView = new Button { Text = "YAML 查看", AutoSize = true };
        btnYamlView.Click += (_, _) => OpenYamlViewer();
        btnRow.Controls.Add(btnYamlView);
        actionRow.Controls.Add(btnRow, 2, 0);

        // 下半区包含操作行与 Proto 编辑区。
        var protoPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        protoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        protoPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        protoPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        protoPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        protoPanel.Controls.Add(actionRow, 0, 0);
        protoPanel.Controls.Add(new Label { Text = "Proto 文本（可编辑）", AutoSize = true }, 0, 1);
        protoPanel.Controls.Add(_txtProto, 0, 2);
        split.Panel2.Controls.Add(protoPanel);

        root.Controls.Add(split);
        return root;
    }

    // 将最小面板尺寸与分割条统一限制在当前窗口允许范围内。
    private static void EnsureSplitConstraintsInRange(
        SplitContainer split,
        int panel1MinSize,
        int panel2MinSize,
        int preferredDistance)
    {
        var total = split.Orientation == Orientation.Horizontal
            ? split.ClientSize.Height
            : split.ClientSize.Width;
        if (total <= 0)
        {
            return;
        }

        // 先按当前总尺寸计算可落地的最小面板尺寸。
        var safePanel1MinSize = Math.Clamp(panel1MinSize, 0, total);
        var safePanel2MinSize = Math.Clamp(panel2MinSize, 0, Math.Max(total - safePanel1MinSize, 0));
        var minDistance = safePanel1MinSize;
        var maxDistance = total - safePanel2MinSize;

        // 总尺寸不足时退化为不限制最小值，保证不会抛出越界异常。
        if (maxDistance < minDistance)
        {
            safePanel1MinSize = 0;
            safePanel2MinSize = 0;
            minDistance = 0;
            maxDistance = total;
        }

        // 先清零约束，再回写安全值，避免设置最小尺寸时触发内部校验异常。
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;

        var targetDistance = split.SplitterDistance;
        if (targetDistance < minDistance || targetDistance > maxDistance)
        {
            targetDistance = preferredDistance;
        }

        split.SplitterDistance = Math.Clamp(targetDistance, minDistance, maxDistance);
        split.Panel1MinSize = safePanel1MinSize;
        split.Panel2MinSize = safePanel2MinSize;
    }

    private async Task ReloadConfigAndProtosAsync()
    {
        try
        {
            _config = _configService.ReadConfig();
            _protoFiles = _protoFileService.CollectProtoFiles(_config.Proto.PathEntries);
            // 直接提取 message 列表，不做预热缓存。
            _messageTypes = ProtocDecoder.ExtractMessageTypes(_protoFiles);
            _cmbMessageType.Items.Clear();
            _cmbMessageType.Items.AddRange(_messageTypes.Cast<object>().ToArray());
            // 成功加载时不显示数量提示，避免干扰界面。
            SetStatus(string.Empty);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        await Task.CompletedTask;
    }

    private void ApplyMessageTypeFilter(string keyword)
    {
        // 过滤过程中忽略重入事件，避免重复刷新。
        if (_isFilteringMessageType)
        {
            return;
        }

        var text = keyword.Trim();
        var filtered = string.IsNullOrEmpty(text)
            ? _messageTypes
            : _messageTypes.Where(x => x.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        _isFilteringMessageType = true;
        try
        {
            _cmbMessageType.BeginUpdate();
            _cmbMessageType.Items.Clear();
            _cmbMessageType.Items.AddRange(filtered.Cast<object>().ToArray());
            _cmbMessageType.DroppedDown = filtered.Count > 0;
            _cmbMessageType.Text = keyword;
            _cmbMessageType.SelectionStart = keyword.Length;
        }
        finally
        {
            _cmbMessageType.EndUpdate();
            _isFilteringMessageType = false;
        }
    }

    private async Task DecodeAsync()
    {
        _txtProto.Text = string.Empty;
        try
        {
            if (_protoFiles.Count == 0)
            {
                throw new InvalidOperationException("未加载任何 proto 文件，请先到设置菜单添加路径。");
            }

            var bytes = Base64Util.Decode(_txtBase64.Text);
            var selectedType = _cmbMessageType.Text.Trim();
            var includeDirs = _protoFileService.GetIncludeDirs(_config.Proto.PathEntries, _protoFiles);

            // message 为必填，禁止自动估分。
            if (string.IsNullOrWhiteSpace(selectedType))
            {
                throw new InvalidOperationException("Message 类型不能为空，请先输入或选择一个 message 类型。");
            }

            if (_messageTypes.Count > 0 && !_messageTypes.Contains(selectedType, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Message 类型不存在：{selectedType}");
            }

            var protoText = _decoder.DecodeToProtoText(bytes, selectedType, includeDirs, _protoFiles);
            _txtProto.Text = protoText;
            SetStatus("解码成功");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        await Task.CompletedTask;
    }

    private async Task EncodeAsync()
    {
        try
        {
            if (_protoFiles.Count == 0)
            {
                throw new InvalidOperationException("未加载任何 proto 文件，请先到设置菜单添加路径。");
            }

            var selectedType = _cmbMessageType.Text.Trim();
            var includeDirs = _protoFileService.GetIncludeDirs(_config.Proto.PathEntries, _protoFiles);
            var protoText = _txtProto.Text;

            // message 为必填，禁止空类型编码。
            if (string.IsNullOrWhiteSpace(selectedType))
            {
                throw new InvalidOperationException("Message 类型不能为空，请先输入或选择一个 message 类型。");
            }

            if (_messageTypes.Count > 0 && !_messageTypes.Contains(selectedType, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Message 类型不存在：{selectedType}");
            }

            var bytes = _decoder.EncodeFromProtoText(protoText, selectedType, includeDirs, _protoFiles);
            _txtBase64.Text = Base64Util.Encode(bytes);
            SetStatus("编码成功");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        await Task.CompletedTask;
    }

    private async Task OpenSettingsDialogAsync()
    {
        using var dialog = new SettingsDialog(
            _configService.GetConfigPath(),
            CloneConfig(_config),
            OnSettingsDialogLayoutChanged);
        var result = dialog.ShowDialog(this);

        try
        {
            if (result == DialogResult.OK && dialog.UpdatedConfig is not null)
            {
                _config = dialog.UpdatedConfig;
                _configService.WriteConfig(_config);
                await ReloadConfigAndProtosAsync();
                SetStatus("配置已保存并重新加载");
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private static AppConfig CloneConfig(AppConfig source)
    {
        return new AppConfig
        {
            Version = source.Version,
            Proto = new ProtoConfig
            {
                Paths = source.Proto.Paths.ToList(),
                PathEntries = source.Proto.PathEntries
                    .Select(x => new ProtoPathEntry
                    {
                        Path = x.Path,
                        IsDirectory = x.IsDirectory,
                        IncludeSubDirectories = x.IncludeSubDirectories
                    })
                    .ToList()
            },
            Ui = new UiConfig
            {
                MainWindow = new WindowLayoutConfig
                {
                    Left = source.Ui.MainWindow.Left,
                    Top = source.Ui.MainWindow.Top,
                    Width = source.Ui.MainWindow.Width,
                    Height = source.Ui.MainWindow.Height,
                    IsMaximized = source.Ui.MainWindow.IsMaximized
                },
                SettingsDialog = new WindowLayoutConfig
                {
                    Left = source.Ui.SettingsDialog.Left,
                    Top = source.Ui.SettingsDialog.Top,
                    Width = source.Ui.SettingsDialog.Width,
                    Height = source.Ui.SettingsDialog.Height,
                    IsMaximized = source.Ui.SettingsDialog.IsMaximized
                },
                YamlViewer = new WindowLayoutConfig
                {
                    Left = source.Ui.YamlViewer.Left,
                    Top = source.Ui.YamlViewer.Top,
                    Width = source.Ui.YamlViewer.Width,
                    Height = source.Ui.YamlViewer.Height,
                    IsMaximized = source.Ui.YamlViewer.IsMaximized
                },
                YamlViewerSplitterDistance = source.Ui.YamlViewerSplitterDistance
            }
        };
    }

    private void OpenYamlViewer()
    {
        try
        {
            var protoText = _txtProto.Text;
            // 空文本不允许转换，避免弹出空窗口。
            if (string.IsNullOrWhiteSpace(protoText))
            {
                throw new InvalidOperationException("Proto 文本为空，无法转换为 YAML。");
            }

            // 将 proto 文本转换为 YAML 文本。
            var yamlText = _yamlConverter.ConvertToYaml(protoText);
            using var viewer = new YamlViewerForm(
                yamlText,
                _config.Ui.YamlViewer,
                _config.Ui.YamlViewerSplitterDistance,
                OnYamlViewerLayoutChanged);
            viewer.ShowDialog(this);
            SetStatus("YAML 转换成功");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    // 记录设置窗口布局并立即持久化。
    private void OnSettingsDialogLayoutChanged(WindowLayoutConfig layout)
    {
        try
        {
            _config.Ui.SettingsDialog = layout;
            _configService.WriteConfig(_config);
        }
        catch (Exception ex)
        {
            SetStatus($"保存设置窗口布局失败：{ex.Message}");
        }
    }

    // 记录 YAML 窗口布局和分栏位置并立即持久化。
    private void OnYamlViewerLayoutChanged(WindowLayoutConfig layout, int splitterDistance)
    {
        try
        {
            _config.Ui.YamlViewer = layout;
            _config.Ui.YamlViewerSplitterDistance = splitterDistance > 0 ? splitterDistance : null;
            _configService.WriteConfig(_config);
        }
        catch (Exception ex)
        {
            SetStatus($"保存 YAML 窗口布局失败：{ex.Message}");
        }
    }

    private void SetStatus(string message)
    {
        // 统一通过底部状态栏输出提示。
        _statusLabel.Text = message;
    }
}
