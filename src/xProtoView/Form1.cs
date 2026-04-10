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

    private readonly MenuStrip _menuStrip = new();
    private readonly ToolStripMenuItem _menuSettings = new("设置");
    // Base64 文本框默认自动换行，便于查看长内容。
    private readonly TextBox _txtBase64 = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Height = 220, Dock = DockStyle.Fill };
    private readonly ComboBox _cmbMessageType = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _txtProto = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, ReadOnly = false, Dock = DockStyle.Fill };
    private readonly Label _lblStatus = new() { AutoSize = true, ForeColor = Color.DarkSlateGray };

    public Form1()
    {
        InitializeComponent();
        BuildUi();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await ReloadConfigAndProtosAsync();
    }

    private void BuildUi()
    {
        Text = "xProtoView (C#)";
        MinimumSize = new Size(1100, 760);
        ClientSize = new Size(1300, 860);
        // 顶部菜单。
        BuildMenu();
        // 主体解析视图。
        Controls.Add(BuildParseView());
        Controls.Add(_menuStrip);
        MainMenuStrip = _menuStrip;
    }

    private void BuildMenu()
    {
        _menuSettings.Click += async (_, _) => await OpenSettingsDialogAsync();
        _menuStrip.Items.Add(_menuSettings);
    }

    private Control BuildParseView()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

        root.Controls.Add(new Label { Text = "Base64", AutoSize = true }, 0, 0);
        root.Controls.Add(_txtBase64, 0, 1);

        var msgRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        msgRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        msgRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        msgRow.Controls.Add(new Label { Text = "Message 类型（必填）", AutoSize = true, Padding = new Padding(0, 8, 8, 0) }, 0, 0);
        _cmbMessageType.Dock = DockStyle.Fill;
        msgRow.Controls.Add(_cmbMessageType, 1, 0);
        root.Controls.Add(msgRow, 0, 2);

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var btnDecode = new Button { Text = "解码（base64->proto）", AutoSize = true };
        btnDecode.Click += async (_, _) => await DecodeAsync();
        btnRow.Controls.Add(btnDecode);
        var btnEncode = new Button { Text = "编码（proto->base64）", AutoSize = true };
        btnEncode.Click += async (_, _) => await EncodeAsync();
        btnRow.Controls.Add(btnEncode);
        var btnYamlView = new Button { Text = "YAML 查看", AutoSize = true };
        btnYamlView.Click += (_, _) => OpenYamlViewer();
        btnRow.Controls.Add(btnYamlView);
        btnRow.Controls.Add(_lblStatus);
        root.Controls.Add(btnRow, 0, 3);

        root.Controls.Add(new Label { Text = "Proto 文本（可编辑）", AutoSize = true }, 0, 4);
        root.Controls.Add(_txtProto, 0, 5);

        return root;
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
            _lblStatus.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _lblStatus.Text = ex.Message;
        }
        await Task.CompletedTask;
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
            _lblStatus.Text = "解码成功";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = ex.Message;
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
            _lblStatus.Text = "编码成功";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = ex.Message;
        }
        await Task.CompletedTask;
    }

    private async Task OpenSettingsDialogAsync()
    {
        using var dialog = new SettingsDialog(_configService.GetConfigPath(), CloneConfig(_config));
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.UpdatedConfig is null)
        {
            return;
        }

        try
        {
            _config = dialog.UpdatedConfig;
            _configService.WriteConfig(_config);
            await ReloadConfigAndProtosAsync();
            _lblStatus.Text = "配置已保存并重新加载";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = ex.Message;
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
            using var viewer = new YamlViewerForm(yamlText);
            viewer.ShowDialog(this);
            _lblStatus.Text = "YAML 转换成功";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = ex.Message;
        }
    }
}
