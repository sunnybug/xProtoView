using System.Text.RegularExpressions;
using xProtoView.Services;
using YamlDotNet.RepresentationModel;

namespace xProtoView;

public sealed class YamlViewerForm : Form
{
    private readonly Action<WindowLayoutConfig, int> _onLayoutChanged;
    private readonly TreeView _treeYaml = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly RichTextBox _txtYaml = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 420 };

    public YamlViewerForm(
        string yamlText,
        WindowLayoutConfig layout,
        int? splitterDistance,
        Action<WindowLayoutConfig, int> onLayoutChanged)
    {
        _onLayoutChanged = onLayoutChanged;
        InitializeWindow();
        BuildLayout();
        // 打开时恢复 YAML 窗口布局。
        WindowLayoutHelper.ApplyLayout(this, layout);
        // 窗口显示后恢复分栏位置，避免越界异常。
        Shown += (_, _) => ApplySplitterDistance(splitterDistance);
        LoadYaml(yamlText);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // 关闭时回传 YAML 窗口布局和分栏位置。
        _onLayoutChanged(WindowLayoutHelper.CaptureLayout(this), _split.SplitterDistance);
    }

    // 初始化弹窗窗口属性。
    private void InitializeWindow()
    {
        Text = "YAML 预览";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 620);
        ClientSize = new Size(1080, 720);
    }

    // 构建工具栏与左右布局。
    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var btnExpand = new Button { Text = "展开全部", AutoSize = true };
        btnExpand.Click += (_, _) => _treeYaml.ExpandAll();
        btnRow.Controls.Add(btnExpand);
        var btnCollapse = new Button { Text = "折叠全部", AutoSize = true };
        btnCollapse.Click += (_, _) => _treeYaml.CollapseAll();
        btnRow.Controls.Add(btnCollapse);
        var btnCopy = new Button { Text = "复制 YAML", AutoSize = true };
        btnCopy.Click += (_, _) => Clipboard.SetText(_txtYaml.Text);
        btnRow.Controls.Add(btnCopy);

        _split.Panel1.Controls.Add(_treeYaml);
        _split.Panel2.Controls.Add(_txtYaml);

        root.Controls.Add(btnRow, 0, 0);
        root.Controls.Add(_split, 0, 1);
        Controls.Add(root);
    }

    // 加载 YAML 文本并构建高亮树。
    private void LoadYaml(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            throw new InvalidOperationException("YAML 文本为空，无法显示。");
        }

        _txtYaml.Font = new Font("Consolas", 10.5f, FontStyle.Regular);
        _txtYaml.Text = yamlText;
        HighlightYamlText(yamlText);

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

    // 递归渲染 YAML 节点到可折叠树结构。
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

    // 对右侧 YAML 文本做简易语法高亮。
    private void HighlightYamlText(string yamlText)
    {
        _txtYaml.SuspendLayout();
        _txtYaml.SelectAll();
        _txtYaml.SelectionColor = Color.Black;

        var lines = yamlText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var offset = 0;
        foreach (var line in lines)
        {
            HighlightYamlLine(line, offset);
            offset += line.Length + 1;
        }

        _txtYaml.Select(0, 0);
        _txtYaml.ResumeLayout();
    }

    // 按“键: 值”规则高亮当前行。
    private void HighlightYamlLine(string line, int lineOffset)
    {
        if (line.Length == 0)
        {
            return;
        }

        var colonIndex = line.IndexOf(':');
        if (colonIndex > 0)
        {
            _txtYaml.Select(lineOffset, colonIndex);
            _txtYaml.SelectionColor = Color.DarkBlue;
            var value = line[(colonIndex + 1)..].Trim();
            if (value.Length > 0)
            {
                var valueStart = line.IndexOf(value, colonIndex + 1, StringComparison.Ordinal);
                _txtYaml.Select(lineOffset + valueStart, value.Length);
                _txtYaml.SelectionColor = ResolveScalarColor(value);
            }
            return;
        }

        if (line.TrimStart().StartsWith("-", StringComparison.Ordinal))
        {
            var value = line.TrimStart()[1..].TrimStart();
            if (value.Length > 0)
            {
                var valueStart = line.IndexOf(value, StringComparison.Ordinal);
                _txtYaml.Select(lineOffset + valueStart, value.Length);
                _txtYaml.SelectionColor = ResolveScalarColor(value);
            }
        }
    }

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

    // 在当前窗口宽度内安全应用分栏距离。
    private void ApplySplitterDistance(int? splitterDistance)
    {
        if (splitterDistance is null)
        {
            return;
        }

        var minDistance = Math.Max(_split.Panel1MinSize, 100);
        var maxDistance = Math.Max(minDistance, _split.Width - Math.Max(_split.Panel2MinSize, 100));
        if (maxDistance <= minDistance)
        {
            return;
        }

        _split.SplitterDistance = Math.Clamp(splitterDistance.Value, minDistance, maxDistance);
    }
}
