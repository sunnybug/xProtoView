using System.Text.RegularExpressions;
using xProtoView.Services;
using YamlDotNet.RepresentationModel;

namespace xProtoView;

public partial class Form1 : Form
{
    private readonly ConfigService _configService = new();
    private readonly ProtoFileService _protoFileService = new();
    private readonly ProtocDecoder _decoder = new();
    private readonly ProtoTextYamlConverter _yamlConverter = new();
    private readonly UpdateService _updateService = new();

    private AppConfig _config = AppConfig.Default();
    private List<string> _protoFiles = [];
    private List<string> _messageTypes = [];
    private bool _isFilteringMessageType;
    private UpdateInfo? _availableUpdate;

    private readonly MenuStrip _menuStrip = new();
    private readonly ToolStripMenuItem _menuSettings = new("设置");
    private readonly ToolStripMenuItem _menuHelp = new("帮助");
    private readonly ToolStripMenuItem _menuUpdate = new("更新") { Visible = false };
    private readonly ToolStripMenuItem _menuAbout = new("关于");
    // 底部状态栏统一承载提示信息。
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    // Base64 文本框默认自动换行，便于查看长内容。
    private readonly TextBox _txtBase64 = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Height = 220, Dock = DockStyle.Fill };
    private readonly ComboBox _cmbMessageType = new() { DropDownStyle = ComboBoxStyle.DropDown };
    // Proto 文本在 Proto 标签页中可编辑。
    private readonly TextBox _txtProto = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, ReadOnly = false, Dock = DockStyle.Fill };
    // YAML 高亮文本在 YAML 标签页中只读展示。
    private readonly RichTextBox _txtYamlHighlighted = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false, BorderStyle = BorderStyle.FixedSingle };
    // YAML 树在折叠标签页中展示层级结构。
    private readonly TreeView _treeYaml = new() { Dock = DockStyle.Fill, HideSelection = false };
    // 右侧使用三标签页承载 YAML/折叠/Proto 三种视图。
    private readonly TabControl _tabProtoView = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabYaml = new("YAML");
    private readonly TabPage _tabYamlFold = new("YAML折叠");
    private readonly TabPage _tabProto = new("Proto");
    private bool _isYamlDirty = true;

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
        await CheckForUpdateOnStartupAsync();
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
        _menuUpdate.Click += async (_, _) => await UpdateToLatestAsync();
        _menuAbout.Click += (_, _) => ShowAboutDialog();
        _menuHelp.DropDownItems.AddRange([_menuUpdate, _menuAbout]);
        _menuStrip.Items.Add(_menuSettings);
        _menuStrip.Items.Add(_menuHelp);
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
        _cmbMessageType.TextUpdate += (_, _) =>
            ApplyMessageTypeFilter(_cmbMessageType.Text, _cmbMessageType.SelectionStart, expandDropDown: true);
        // 手动展开下拉时强制显示全量列表，避免输入文本导致候选被隐藏。
        _cmbMessageType.DropDown += (_, _) =>
        {
            // 输入触发的程序化展开不干预，保持当前过滤结果。
            if (_isFilteringMessageType)
            {
                return;
            }
            // 延迟到展开流程之后刷新，避免打断原生展开行为。
            BeginInvoke(() => ApplyMessageTypeFilter(string.Empty, null, expandDropDown: true));
        };
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
        actionRow.Controls.Add(btnRow, 2, 0);

        // 下半区包含操作行与三标签页预览区。
        var protoPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        protoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        protoPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        protoPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        protoPanel.Controls.Add(actionRow, 0, 0);
        protoPanel.Controls.Add(BuildProtoTabs(), 0, 1);
        split.Panel2.Controls.Add(protoPanel);

        root.Controls.Add(split);
        return root;
    }

    // 构建 YAML/折叠/Proto 三标签页。
    private Control BuildProtoTabs()
    {
        ConfigureProtoTabsAppearance();
        BuildYamlTab();
        BuildYamlFoldTab();
        BuildProtoTab();
        _tabProtoView.TabPages.Clear();
        _tabProtoView.TabPages.AddRange([_tabYaml, _tabYamlFold, _tabProto]);
        // 默认进入 YAML 标签页。
        _tabProtoView.SelectedTab = _tabYaml;
        _tabProtoView.SelectedIndexChanged += (_, _) => OnProtoViewTabChanged();
        // Proto 文本变更后标记 YAML 需重新生成。
        _txtProto.TextChanged += (_, _) => _isYamlDirty = true;
        // 初始为空时显示占位提示。
        ShowYamlMessage("Proto 文本为空，暂无 YAML 预览。");
        return _tabProtoView;
    }

    // 统一设置标签页外观，让标签间隔和选中态更清晰。
    private void ConfigureProtoTabsAppearance()
    {
        _tabProtoView.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabProtoView.Padding = new Point(16, 6);
        _tabProtoView.DrawItem -= DrawProtoTabItem;
        _tabProtoView.DrawItem += DrawProtoTabItem;
    }

    // 自绘标签标题并预留左右空隙，增强标签间视觉分隔。
    private void DrawProtoTabItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _tabProtoView.TabPages.Count)
        {
            return;
        }

        var tabPage = _tabProtoView.TabPages[e.Index];
        var isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var tabRect = Rectangle.Inflate(e.Bounds, -4, -1);
        var backColor = isSelected ? Color.White : Color.FromArgb(245, 245, 245);
        var borderColor = isSelected ? SystemColors.Highlight : Color.Silver;

        using var backBrush = new SolidBrush(backColor);
        using var borderPen = new Pen(borderColor);
        e.Graphics.FillRectangle(backBrush, tabRect);
        e.Graphics.DrawRectangle(borderPen, tabRect);
        TextRenderer.DrawText(
            e.Graphics,
            tabPage.Text,
            _tabProtoView.Font,
            tabRect,
            SystemColors.ControlText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    // 构建 YAML 高亮文本标签页。
    private void BuildYamlTab()
    {
        _tabYaml.Controls.Clear();
        var yamlPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        yamlPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        yamlPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        yamlPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        yamlPanel.Controls.Add(new Label { Text = "YAML 预览（高亮）", AutoSize = true }, 0, 0);
        _txtYamlHighlighted.Font = new Font("Consolas", 10.5f, FontStyle.Regular);
        yamlPanel.Controls.Add(_txtYamlHighlighted, 0, 1);
        _tabYaml.Controls.Add(yamlPanel);
    }

    // 构建 YAML 折叠树标签页。
    private void BuildYamlFoldTab()
    {
        _tabYamlFold.Controls.Clear();
        var foldPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        foldPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foldPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foldPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        var btnExpand = new Button { Text = "展开全部", AutoSize = true };
        btnExpand.Click += (_, _) => _treeYaml.ExpandAll();
        btnRow.Controls.Add(btnExpand);
        var btnCollapse = new Button { Text = "折叠全部", AutoSize = true };
        btnCollapse.Click += (_, _) => _treeYaml.CollapseAll();
        btnRow.Controls.Add(btnCollapse);

        foldPanel.Controls.Add(btnRow, 0, 0);
        foldPanel.Controls.Add(_treeYaml, 0, 1);
        _tabYamlFold.Controls.Add(foldPanel);
    }

    // 构建 Proto 编辑标签页并放置编码按钮。
    private void BuildProtoTab()
    {
        _tabProto.Controls.Clear();
        var protoPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        protoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        protoPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        protoPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        protoPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        var btnEncode = new Button { Text = "proto->base64", AutoSize = true };
        btnEncode.Click += async (_, _) => await EncodeAsync();
        btnRow.Controls.Add(btnEncode);

        protoPanel.Controls.Add(btnRow, 0, 0);
        protoPanel.Controls.Add(new Label { Text = "Proto 文本（可编辑）", AutoSize = true }, 0, 1);
        protoPanel.Controls.Add(_txtProto, 0, 2);
        _tabProto.Controls.Add(protoPanel);
    }

    // 切换到 YAML 相关标签页时刷新 YAML 预览。
    private void OnProtoViewTabChanged()
    {
        if (_tabProtoView.SelectedTab != _tabYaml && _tabProtoView.SelectedTab != _tabYamlFold)
        {
            return;
        }

        if (!TryRefreshYamlViews(force: false, out var error) && !string.IsNullOrWhiteSpace(error))
        {
            SetStatus(error);
        }
    }

    // 按需将当前 proto 文本转换为 YAML 并刷新两个标签页。
    private bool TryRefreshYamlViews(bool force, out string? error)
    {
        error = null;
        if (!force && !_isYamlDirty)
        {
            return true;
        }

        var protoText = _txtProto.Text;
        if (string.IsNullOrWhiteSpace(protoText))
        {
            ShowYamlMessage("Proto 文本为空，暂无 YAML 预览。");
            _isYamlDirty = false;
            return true;
        }

        try
        {
            // 使用统一转换器生成 YAML 文本。
            var yamlText = _yamlConverter.ConvertToYaml(protoText);
            RenderYamlHighlightedText(yamlText);
            RenderYamlTree(yamlText);
            _isYamlDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            error = $"YAML 转换失败：{ex.Message}";
            ShowYamlMessage(error);
            _isYamlDirty = false;
            return false;
        }
    }

    // 显示 YAML 占位或错误信息。
    private void ShowYamlMessage(string message)
    {
        _txtYamlHighlighted.Text = message;
        _txtYamlHighlighted.SelectAll();
        _txtYamlHighlighted.SelectionColor = Color.Gray;
        _txtYamlHighlighted.Select(0, 0);
        _treeYaml.BeginUpdate();
        _treeYaml.Nodes.Clear();
        _treeYaml.Nodes.Add(new TreeNode(message) { ForeColor = Color.Gray });
        _treeYaml.EndUpdate();
    }

    // 渲染 YAML 高亮文本。
    private void RenderYamlHighlightedText(string yamlText)
    {
        _txtYamlHighlighted.Text = yamlText;
        HighlightYamlText(yamlText);
    }

    // 解析 YAML 文本并渲染为可折叠树。
    private void RenderYamlTree(string yamlText)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yamlText);
            stream.Load(reader);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"YAML 解析失败：{ex.Message}");
        }

        _treeYaml.BeginUpdate();
        _treeYaml.Nodes.Clear();
        var rootNode = new TreeNode("root") { ForeColor = Color.DarkBlue };
        _treeYaml.Nodes.Add(rootNode);
        if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is not null)
        {
            AppendYamlNode(rootNode, stream.Documents[0].RootNode);
        }
        rootNode.Expand();
        _treeYaml.EndUpdate();
    }

    // 对 YAML 文本执行简易语法高亮。
    private void HighlightYamlText(string yamlText)
    {
        _txtYamlHighlighted.SuspendLayout();
        _txtYamlHighlighted.SelectAll();
        _txtYamlHighlighted.SelectionColor = Color.Black;

        var lines = yamlText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var offset = 0;
        foreach (var line in lines)
        {
            HighlightYamlLine(line, offset);
            offset += line.Length + 1;
        }

        _txtYamlHighlighted.Select(0, 0);
        _txtYamlHighlighted.ResumeLayout();
    }

    // 按“键: 值”规则高亮 YAML 每一行。
    private void HighlightYamlLine(string line, int lineOffset)
    {
        if (line.Length == 0)
        {
            return;
        }

        var colonIndex = line.IndexOf(':');
        if (colonIndex > 0)
        {
            _txtYamlHighlighted.Select(lineOffset, colonIndex);
            _txtYamlHighlighted.SelectionColor = Color.DarkBlue;
            var value = line[(colonIndex + 1)..].Trim();
            if (value.Length > 0)
            {
                var valueStart = line.IndexOf(value, colonIndex + 1, StringComparison.Ordinal);
                _txtYamlHighlighted.Select(lineOffset + valueStart, value.Length);
                _txtYamlHighlighted.SelectionColor = ResolveScalarColor(value);
            }
            return;
        }

        if (line.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            var value = line.TrimStart()[1..].TrimStart();
            if (value.Length > 0)
            {
                var valueStart = line.IndexOf(value, StringComparison.Ordinal);
                _txtYamlHighlighted.Select(lineOffset + valueStart, value.Length);
                _txtYamlHighlighted.SelectionColor = ResolveScalarColor(value);
            }
        }
    }

    // 递归渲染 YAML 节点到 TreeView。
    private static void AppendYamlNode(TreeNode parent, YamlNode node)
    {
        if (node is YamlMappingNode mapNode)
        {
            foreach (var item in mapNode.Children)
            {
                var keyText = (item.Key as YamlScalarNode)?.Value ?? "<key>";
                var keyNode = new TreeNode(keyText) { ForeColor = Color.DarkBlue };
                parent.Nodes.Add(keyNode);
                AppendValueNode(keyNode, item.Value);
            }
            return;
        }

        if (node is YamlSequenceNode seqNode)
        {
            var index = 0;
            foreach (var child in seqNode.Children)
            {
                var itemNode = new TreeNode($"[{index}]") { ForeColor = Color.DimGray };
                parent.Nodes.Add(itemNode);
                AppendValueNode(itemNode, child);
                index++;
            }
            return;
        }

        if (node is YamlScalarNode scalarNode)
        {
            var leafNode = new TreeNode(FormatScalarText(scalarNode))
            {
                ForeColor = ResolveScalarColor(scalarNode)
            };
            parent.Nodes.Add(leafNode);
        }
    }

    // 渲染 value 节点并复用同一套递归逻辑。
    private static void AppendValueNode(TreeNode parent, YamlNode valueNode)
    {
        if (valueNode is YamlScalarNode scalarNode)
        {
            parent.Text = $"{parent.Text}: {FormatScalarText(scalarNode)}";
            parent.ForeColor = Color.DarkBlue;
            return;
        }
        AppendYamlNode(parent, valueNode);
    }

    // 处理需要保留空白的标量显示。
    private static string FormatScalarText(YamlScalarNode scalarNode)
    {
        var value = scalarNode.Value ?? "null";
        if (Regex.IsMatch(value, @"^\s|\s$"))
        {
            return $"\"{value}\"";
        }
        return value;
    }

    private static Color ResolveScalarColor(YamlScalarNode scalarNode)
    {
        return ResolveScalarColor(scalarNode.Value ?? "null");
    }

    // 按标量值类型返回高亮颜色。
    private static Color ResolveScalarColor(string value)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return Color.Teal;
        }

        if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || value == "~")
        {
            return Color.Gray;
        }

        if (double.TryParse(value, out _))
        {
            return Color.MediumVioletRed;
        }

        if (value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
        {
            return Color.SaddleBrown;
        }

        return Color.DarkGreen;
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

    // 启动时检查新版本，仅在可自动更新时显示菜单。
    private async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            _availableUpdate = await _updateService.CheckForUpdateAsync(UpdateService.GetCurrentVersion());
            if (_availableUpdate is null)
            {
                _menuUpdate.Visible = false;
                return;
            }

            _menuUpdate.Visible = true;
            _menuUpdate.Text = $"更新（{_availableUpdate.LatestVersion}）";
            SetStatus($"发现新版本：{_availableUpdate.LatestVersion}");
        }
        catch (Exception ex)
        {
            _menuUpdate.Visible = false;
            SetStatus($"检查更新失败：{ex.Message}");
        }
    }

    private void ApplyMessageTypeFilter(string keyword, int? caretPosition, bool expandDropDown = false)
    {
        // 过滤过程中忽略重入事件，避免重复刷新。
        if (_isFilteringMessageType)
        {
            return;
        }

        // 将空白分隔文本视为多关键字，要求全部命中（无顺序要求）。
        var keywords = keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = keywords.Length == 0
            ? _messageTypes
            : _messageTypes.Where(x => keywords.All(k => x.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();

        _isFilteringMessageType = true;
        try
        {
            _cmbMessageType.BeginUpdate();
            _cmbMessageType.Items.Clear();
            _cmbMessageType.Items.AddRange(filtered.Cast<object>().ToArray());
            if (caretPosition.HasValue)
            {
                // 刷新候选项后恢复光标，避免新输入字符插到文本开头。
                _cmbMessageType.SelectionStart = Math.Clamp(caretPosition.Value, 0, _cmbMessageType.Text.Length);
                _cmbMessageType.SelectionLength = 0;
            }
            if (expandDropDown)
            {
                // 仅在需要时保持展开，避免打断系统原生下拉流程。
                _cmbMessageType.DroppedDown = filtered.Count > 0;
            }
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
        // 先清空 YAML 预览，避免失败后保留旧结果。
        _ = TryRefreshYamlViews(force: true, out _);
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
            if (TryRefreshYamlViews(force: true, out var yamlError))
            {
                SetStatus("解码成功");
            }
            else
            {
                SetStatus($"解码成功，但{yamlError}");
            }
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

    // 用户确认后开始更新并退出当前进程。
    private async Task UpdateToLatestAsync()
    {
        try
        {
            if (_availableUpdate is null)
            {
                throw new InvalidOperationException("当前没有可用更新。");
            }

            var result = MessageBox.Show(
                this,
                $"检测到新版本 {_availableUpdate.LatestVersion}，是否立即更新？{Environment.NewLine}当前版本：{_availableUpdate.CurrentVersion}",
                "更新确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (result != DialogResult.Yes)
            {
                SetStatus("已取消更新");
                return;
            }

            SetStatus("正在准备更新，请稍候...");
            await _updateService.StartUpdateAsync(_availableUpdate);
            MessageBox.Show(
                this,
                "更新任务已启动，程序关闭后将自动替换并重启。",
                "更新",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            SetStatus($"更新失败：{ex.Message}");
            MessageBox.Show(this, $"更新失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // “关于”窗口展示项目基础信息。
    private void ShowAboutDialog()
    {
        var message =
            $"工程名：{UpdateService.ProjectName}{Environment.NewLine}" +
            $"GitHub：{UpdateService.RepositoryUrl}{Environment.NewLine}" +
            $"作者：{UpdateService.Author}{Environment.NewLine}" +
            $"版本：{UpdateService.GetCurrentVersion()}";
        MessageBox.Show(this, message, "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    private void SetStatus(string message)
    {
        // 统一通过底部状态栏输出提示。
        _statusLabel.Text = message;
    }
}
