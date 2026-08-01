using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class UiProgram
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private const string AppVersion = "v1.0.4";
    private const string MonitorExe = "napcat-monitor-v1.0.4.exe";
    private const int DefaultTriggerCooldownMs = 3000;
    private readonly string _root = AppDomain.CurrentDomain.BaseDirectory;
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
    private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();

    private Config _config;
    private BindingList<ClickStep> _steps = new BindingList<ClickStep>();

    private TextBox _groupId;
    private TextBox _senderQqs;
    private TextBox _senderNames;
    private TextBox _keywords;
    private ComboBox _keywordMatch;
    private CheckBox _ignoreCase;
    private CheckBox _automationEnabled;
    private TextBox _gameProcess;
    private NumericUpDown _expectedWidth;
    private NumericUpDown _expectedHeight;
    private NumericUpDown _focusWait;
    private NumericUpDown _triggerCooldown;
    private DataGridView _grid;
    private DataGridView _hitsGrid;
    private RadioButton _safeMode;
    private RadioButton _formalMode;
    private TextBox _napcatQq;
    private TextBox _status;
    private TextBox _log;

    public MainForm()
    {
        Text = "QQ抓取 NapCat " + AppVersion + " - 绿头君";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 720);
        Size = new Size(1180, 780);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        LoadConfigToUi();
        RefreshStatus();

        _timer.Interval = 3000;
        _timer.Tick += delegate { RefreshStatus(); };
        _timer.Start();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildDashboardPage());
        tabs.TabPages.Add(BuildConfigPage());
        tabs.TabPages.Add(BuildClickPage());
        tabs.TabPages.Add(BuildHitsPage());
        tabs.TabPages.Add(BuildGuidePage());
        root.Controls.Add(tabs, 0, 0);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 12, 0),
            Text = "版本：" + AppVersion + "    作者：绿头君    QQ：3630375135"
        }, 0, 1);

        Controls.Add(root);
    }

    private TabPage BuildDashboardPage()
    {
        var page = new TabPage("运行");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        _status = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        root.Controls.Add(_status, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(new Label { Text = "NapCat QQ号", Width = 86, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(4) });
        _napcatQq = new TextBox { Width = 120, Height = 32, Margin = new Padding(4) };
        buttons.Controls.Add(_napcatQq);
        buttons.Controls.Add(Button("保存NapCat配置", SaveNapCatConfig));
        buttons.Controls.Add(Button("刷新状态", RefreshStatus));
        buttons.Controls.Add(Button("启动 NapCat", StartNapCat));
        buttons.Controls.Add(Button("启动监听", StartMonitor));
        buttons.Controls.Add(Button("停止监听", StopMonitor));
        buttons.Controls.Add(Button("运行自测", delegate { RunTool("--self-test", true); }));
        buttons.Controls.Add(Button("打开日志目录", OpenLogs));
        buttons.Controls.Add(Button("保存配置", SaveConfigFromUi));
        root.Controls.Add(buttons, 0, 1);

        _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        root.Controls.Add(_log, 0, 2);

        var warn = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "正式监听启用后，命中关键词会直接执行点击。测试前请先用“坐标预览/模拟流程”。"
        };
        root.Controls.Add(warn, 0, 3);

        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildHitsPage()
    {
        var page = new TabPage("命中记录");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _hitsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        root.Controls.Add(_hitsGrid, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(Button("刷新命中记录", LoadHits));
        buttons.Controls.Add(Button("打开日志目录", OpenLogs));
        root.Controls.Add(buttons, 0, 1);

        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildConfigPage()
    {
        var page = new TabPage("监听设置");
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _groupId = TextBox();
        AddRow(panel, "群号", _groupId);

        _senderQqs = TextBox(true, 70);
        AddRow(panel, "监听QQ号", _senderQqs);

        _senderNames = TextBox(true, 60);
        AddRow(panel, "监听群名片", _senderNames);

        _keywords = TextBox(true, 60);
        AddRow(panel, "关键词", _keywords);

        _keywordMatch = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _keywordMatch.Items.AddRange(new object[] { "all", "any" });
        AddRow(panel, "匹配方式", _keywordMatch);

        _ignoreCase = new CheckBox { Text = "忽略大小写" };
        AddRow(panel, "大小写", _ignoreCase);

        _automationEnabled = new CheckBox { Text = "命中后自动点击" };
        AddRow(panel, "自动点击", _automationEnabled);

        var modePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 36 };
        _safeMode = new RadioButton { Text = "安全模式：只监听不点击", Width = 180, Checked = true };
        _formalMode = new RadioButton { Text = "正式模式：命中后点击", Width = 180 };
        _safeMode.CheckedChanged += delegate { if (_safeMode.Checked) _automationEnabled.Checked = false; };
        _formalMode.CheckedChanged += delegate { if (_formalMode.Checked) _automationEnabled.Checked = true; };
        _automationEnabled.CheckedChanged += delegate
        {
            if (_automationEnabled.Checked) _formalMode.Checked = true;
            else _safeMode.Checked = true;
        };
        modePanel.Controls.Add(_safeMode);
        modePanel.Controls.Add(_formalMode);
        AddRow(panel, "运行模式", modePanel);

        _gameProcess = TextBox();
        AddRow(panel, "游戏进程", _gameProcess);

        _expectedWidth = Number(0, 10000);
        AddRow(panel, "窗口宽度", _expectedWidth);

        _expectedHeight = Number(0, 10000);
        AddRow(panel, "窗口高度", _expectedHeight);

        var resolutionButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 38 };
        resolutionButtons.Controls.Add(Button("自动读取游戏分辨率", DetectGameResolution, 160));
        AddRow(panel, "", resolutionButtons);

        _focusWait = Number(0, 5000);
        AddRow(panel, "激活等待ms", _focusWait);

        _triggerCooldown = Number(0, 600000);
        AddRow(panel, "触发冷却ms", _triggerCooldown);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 46 };
        buttons.Controls.Add(Button("保存配置", SaveConfigFromUi));
        buttons.Controls.Add(Button("重新读取", LoadConfigToUi));
        AddRow(panel, "", buttons);

        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildClickPage()
    {
        var page = new TabPage("点击设置");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        AddColumn("name", "名称", 120);
        AddColumn("x", "X", 70);
        AddColumn("y", "Y", 70);
        AddColumn("delay_ms", "点击前等待ms", 110);
        AddColumn("clicks", "点击次数", 80);
        AddColumn("interval_ms", "连点间隔ms", 100);
        root.Controls.Add(_grid, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(Button("保存点击配置", SaveClickConfigFromUi));
        buttons.Controls.Add(Button("采集点击位置", delegate { RunTool("--capture-points"); }));
        buttons.Controls.Add(Button("预览已采集位置", delegate { RunTool("--preview-captured-points"); }));
        buttons.Controls.Add(Button("模拟点击流程", delegate { RunTool("--dry-run-clicks", true); }));
        buttons.Controls.Add(Button("真实点击测试", delegate { RunTool("--test-captured-clicks"); }));
        buttons.Controls.Add(Button("设置等待时间", delegate { RunTool("--edit-captured-delays"); }));
        buttons.Controls.Add(Button("清空点击位置", ClearCapturedPoints));
        root.Controls.Add(buttons, 0, 1);

        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildGuidePage()
    {
        var page = new TabPage("使用指南");
        var guide = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = "用户需要自行完成：\r\n" +
                   "1. 安装并登录 QQ，加入指定群聊。\r\n" +
                   "2. 安装并登录游戏，进入需要点击的页面。\r\n" +
                   "3. 在“点击设置”里采集本机坐标并设置等待时间。\r\n\r\n" +
                   "推荐流程：\r\n" +
                   "1. 打开本 UI，刷新状态，确认 NapCat 端口 3001 正常。\r\n" +
                   "2. 在“监听设置”保存群号、监听 QQ、关键词。\r\n" +
                   "3. 在“点击设置”采集坐标，先预览，再模拟，最后才真实测试。\r\n" +
                   "4. 回到“运行”页启动监听。\r\n\r\n" +
                   "状态说明：\r\n" +
                   "NapCat 在线并且 3001 端口可连接，才说明监听通道可用。\r\n" +
                   "游戏进程存在不代表页面正确，正式运行前必须人工确认游戏界面。\r\n" +
                   "触发冷却期间的新命中会被忽略，避免连续重复点击。\r\n\r\n" +
                   "版本：" + AppVersion + "\r\n" +
                   "作者：绿头君\r\n" +
                   "QQ：3630375135"
        };
        page.Controls.Add(guide);
        return page;
    }

    private void AddColumn(string property, string title, int width)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = title,
            FillWeight = width
        });
    }

    private static Button Button(string text, Action action)
    {
        return Button(text, action, 120);
    }

    private static Button Button(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 32, Margin = new Padding(4) };
        button.Click += delegate { action(); };
        return button;
    }

    private static TextBox TextBox(bool multiline, int height)
    {
        return new TextBox { Dock = DockStyle.Fill, Multiline = multiline, Height = height, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None };
    }

    private static TextBox TextBox()
    {
        return new TextBox { Dock = DockStyle.Fill };
    }

    private static NumericUpDown Number(int min, int max)
    {
        return new NumericUpDown { Dock = DockStyle.Left, Minimum = min, Maximum = max, Width = 140 };
    }

    private static void AddRow(TableLayoutPanel panel, string label, Control control)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Height = Math.Max(32, control.Height + 8) };
        panel.Controls.Add(l, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private void LoadConfigToUi()
    {
        try
        {
            _config = LoadConfig();
            _groupId.Text = _config.group_id.ToString();
            _senderQqs.Text = JoinLines(_config.sender_qqs);
            _senderNames.Text = String.Join(Environment.NewLine, _config.sender_names ?? new string[0]);
            _keywords.Text = String.Join(Environment.NewLine, _config.keywords ?? new string[0]);
            _keywordMatch.SelectedItem = String.IsNullOrWhiteSpace(_config.keyword_match) ? "all" : _config.keyword_match;
            _ignoreCase.Checked = _config.ignore_case;
            _automationEnabled.Checked = _config.automation_enabled;
            _safeMode.Checked = !_config.automation_enabled;
            _formalMode.Checked = _config.automation_enabled;
            _gameProcess.Text = _config.game_process_name;
            _expectedWidth.Value = Clamp(_config.expected_client_width, _expectedWidth);
            _expectedHeight.Value = Clamp(_config.expected_client_height, _expectedHeight);
            _focusWait.Value = Clamp(_config.focus_wait_ms, _focusWait);
            _triggerCooldown.Value = Clamp(_config.trigger_cooldown_ms.HasValue ? _config.trigger_cooldown_ms.Value : DefaultTriggerCooldownMs, _triggerCooldown);
            _steps = new BindingList<ClickStep>(_config.click_steps ?? new List<ClickStep>());
            _grid.DataSource = _steps;
            LoadCapturedPointsToUi(false);
            LoadHits();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取配置失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveConfigFromUi()
    {
        if (TrySaveConfigFromUi(true, true))
            MessageBox.Show("配置已保存。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveClickConfigFromUi()
    {
        LoadCapturedPointsToUi(false);
        SaveConfigFromUi();
    }

    private void DetectGameResolution()
    {
        string processName = (_gameProcess.Text ?? "").Trim();
        IntPtr window = FindGameWindow(processName);
        if (window == IntPtr.Zero)
        {
            MessageBox.Show("未找到游戏窗口：" + processName + "\r\n请先启动游戏，并确认游戏进程名填写正确。", "未找到窗口", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        NativeRect rect;
        if (!GetClientRect(window, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            MessageBox.Show("无法读取游戏窗口客户区尺寸。", "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        _expectedWidth.Value = Clamp(width, _expectedWidth);
        _expectedHeight.Value = Clamp(height, _expectedHeight);
        if (TrySaveConfigFromUi(false, false))
            MessageBox.Show("已读取并保存游戏分辨率：" + width + "x" + height, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show("已读取到界面：" + width + "x" + height + "\r\n但其它配置未通过校验，请检查后手动保存配置。", "已读取", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private bool TrySaveConfigFromUi(bool showErrors, bool confirmFormal)
    {
        try
        {
            _grid.EndEdit();
            long groupId;
            if (!long.TryParse(_groupId.Text.Trim(), out groupId) || groupId <= 0)
                throw new InvalidDataException("群号无效。");

            _config.group_id = groupId;
            _config.sender_qqs = ParseLongs(_senderQqs.Text);
            _config.sender_names = ParseStrings(_senderNames.Text);
            _config.keywords = ParseStrings(_keywords.Text);
            _config.keyword_match = Convert.ToString(_keywordMatch.SelectedItem);
            _config.ignore_case = _ignoreCase.Checked;
            _config.automation_enabled = _automationEnabled.Checked;
            _config.game_process_name = _gameProcess.Text.Trim();
            _config.expected_client_width = (int)_expectedWidth.Value;
            _config.expected_client_height = (int)_expectedHeight.Value;
            _config.focus_wait_ms = (int)_focusWait.Value;
            _config.trigger_cooldown_ms = (int)_triggerCooldown.Value;
            _config.click_steps = CleanSteps(_steps);

            if (_config.sender_qqs.Length == 0 && _config.sender_names.Length == 0)
                throw new InvalidDataException("监听 QQ 号和群名片至少填一项。");
            if (_config.keywords.Length == 0)
                throw new InvalidDataException("关键词至少填一项。");
            if (_config.automation_enabled && _config.click_steps.Count == 0)
                throw new InvalidDataException("启用自动点击前必须至少有一个点击步骤。");
            if (confirmFormal && _config.automation_enabled &&
                MessageBox.Show("将开启正式模式：命中关键词后会执行 " + _config.click_steps.Count + " 步真实点击。\r\n关键词：" +
                    String.Join(" / ", _config.keywords) + "\r\n确认继续？", "正式模式确认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return false;

            SaveConfig(_config);
            SaveCapturedPoints(_config);
            RefreshStatus();
            return true;
        }
        catch (Exception ex)
        {
            if (showErrors)
                MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private Config LoadConfig()
    {
        string path = Path.Combine(_root, "config.json");
        return _json.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8));
    }

    private void SaveConfig(Config config)
    {
        File.WriteAllText(Path.Combine(_root, "config.json"), _json.Serialize(config), new UTF8Encoding(false));
    }

    private void SaveCapturedPoints(Config config)
    {
        var captured = new CapturedPoints
        {
            game_process_name = config.game_process_name,
            expected_client_width = config.expected_client_width,
            expected_client_height = config.expected_client_height,
            click_steps = config.click_steps
        };
        File.WriteAllText(Path.Combine(_root, "点击位置待确认.json"), _json.Serialize(captured), new UTF8Encoding(false));
    }

    private bool LoadCapturedPointsToUi(bool showErrors)
    {
        string path = Path.Combine(_root, "点击位置待确认.json");
        if (!File.Exists(path)) return false;
        try
        {
            CapturedPoints captured = _json.Deserialize<CapturedPoints>(File.ReadAllText(path, Encoding.UTF8));
            if (captured == null || captured.click_steps == null) return false;
            if (!String.IsNullOrWhiteSpace(captured.game_process_name)) _gameProcess.Text = captured.game_process_name;
            _expectedWidth.Value = Clamp(captured.expected_client_width, _expectedWidth);
            _expectedHeight.Value = Clamp(captured.expected_client_height, _expectedHeight);
            _steps = new BindingList<ClickStep>(captured.click_steps);
            _grid.DataSource = _steps;
            if (_config != null)
            {
                _config.game_process_name = _gameProcess.Text.Trim();
                _config.expected_client_width = (int)_expectedWidth.Value;
                _config.expected_client_height = (int)_expectedHeight.Value;
                _config.click_steps = new List<ClickStep>(_steps);
            }
            return true;
        }
        catch (Exception ex)
        {
            if (showErrors) MessageBox.Show("读取点击位置失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void RefreshStatus()
    {
        try
        {
            if (_config == null && File.Exists(Path.Combine(_root, "config.json"))) _config = LoadConfig();
            string gameName = _config == null ? "infinite_lagrange_cn" : _config.game_process_name;
            string botJson = @"C:\ProgramData\NapCatQQ Desktop\config\bot.json";

            var sb = new StringBuilder();
            sb.AppendLine("软件版本：" + AppVersion);
            sb.AppendLine("运行状态：" + RuntimeStateText());
            sb.AppendLine("NapCat进程：" + YesNo(HasNapCatProcess()));
            sb.AppendLine("NapCat监听端口3001：" + YesNo(CanConnect("127.0.0.1", 3001, 250)));
            string napcatQq = ReadQqId(botJson);
            if (_napcatQq != null && napcatQq != "未找到配置" && napcatQq != "未配置") _napcatQq.Text = napcatQq;
            sb.AppendLine("NapCat配置QQ：" + napcatQq);
            sb.AppendLine("监听程序运行：" + YesNo(HasMonitorProcess()));
            sb.AppendLine("游戏进程(" + gameName + ")：" + YesNo(HasProcess(Path.GetFileNameWithoutExtension(gameName))));
            sb.AppendLine("点击步骤：" + ((_config == null || _config.click_steps == null) ? 0 : _config.click_steps.Count) + " 步");
            sb.AppendLine("触发冷却：" + ((_config == null || !_config.trigger_cooldown_ms.HasValue) ? DefaultTriggerCooldownMs : _config.trigger_cooldown_ms.Value) + "ms");
            sb.AppendLine("自动点击：" + ((_config != null && _config.automation_enabled) ? "启用" : "关闭"));
            sb.AppendLine("关键词：" + ((_config == null || _config.keywords == null) ? "" : String.Join(" / ", _config.keywords)));
            _status.Text = sb.ToString();
            _log.Text = Tail(Path.Combine(_root, "日志", "运行日志-" + AppVersion + ".txt"), 80);
        }
        catch (Exception ex)
        {
            _status.Text = "状态刷新失败：" + ex.Message;
        }
    }

    private void StartNapCat()
    {
        string exe = @"C:\Program Files\NapCatQQ Desktop\NapCatQQ-Desktop.exe";
        if (!File.Exists(exe))
        {
            MessageBox.Show("未找到 NapCat：" + exe, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        RefreshStatus();
    }

    private void SaveNapCatConfig()
    {
        string qq = (_napcatQq.Text ?? "").Trim();
        if (!Regex.IsMatch(qq, "^\\d{5,12}$"))
        {
            MessageBox.Show("请填写正确的监听 QQ 号。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string desktopRoot = @"C:\ProgramData\NapCatQQ Desktop";
        string botConfigDir = Path.Combine(desktopRoot, "config");
        string napcatConfigDir = FindNapCatConfigDir(desktopRoot);

        try
        {
            Directory.CreateDirectory(botConfigDir);
            Directory.CreateDirectory(napcatConfigDir);
            string botJson = Path.Combine(botConfigDir, "bot.json");
            if (File.Exists(botJson))
            {
                string backup = Path.Combine(botConfigDir, "bot.json.before-ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak");
                File.Copy(botJson, backup, true);
            }

            File.WriteAllText(botJson, BuildBotJson(qq), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(napcatConfigDir, "onebot11_" + qq + ".json"), BuildOneBotJson(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(napcatConfigDir, "napcat_" + qq + ".json"), BuildNapCatJson(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(napcatConfigDir, "napcat_protocol_" + qq + ".json"), BuildProtocolJson(), new UTF8Encoding(false));

            MessageBox.Show("NapCat 配置已写入。\r\n如果 NapCat bot 还没下载，先启动 NapCat 等它下载组件；组件生成后配置会放在同一目录规则下。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show("写入 NapCat 配置失败：" + ex.Message + "\r\n如果是权限问题，请右键 UI 以管理员身份运行。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string FindNapCatConfigDir(string desktopRoot)
    {
        string preferred = Path.Combine(desktopRoot, "components", "NapCatQQ", "config");
        if (Directory.Exists(preferred)) return preferred;

        string components = Path.Combine(desktopRoot, "components");
        if (Directory.Exists(components))
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(components, "config", SearchOption.AllDirectories))
                    if (dir.IndexOf("NapCat", StringComparison.OrdinalIgnoreCase) >= 0)
                        return dir;
            }
            catch { }
        }

        return preferred;
    }

    private void StartMonitor()
    {
        if (HasMonitorProcess())
        {
            MessageBox.Show("监听程序已经在运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string errors = ValidateBeforeStart();
        if (errors.Length > 0)
        {
            MessageBox.Show(errors, "启动前检查未通过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_config.automation_enabled &&
            MessageBox.Show("正式模式已开启。启动后命中关键词会执行 " + _config.click_steps.Count + " 步真实点击。\r\n确认启动监听？",
                "启动确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        string exe = Path.Combine(_root, MonitorExe);
        if (!File.Exists(exe))
        {
            MessageBox.Show("找不到：" + exe, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = _root, UseShellExecute = true });
        RefreshStatus();
    }

    private void StopMonitor()
    {
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.StartsWith("napcat-monitor-v", StringComparison.OrdinalIgnoreCase))
                    p.Kill();
            }
            catch { }
        }
        RefreshStatus();
    }

    private void RunTool(string args)
    {
        RunTool(args, false);
    }

    private void RunTool(string args, bool captureOutput)
    {
        string exe = Path.Combine(_root, MonitorExe);
        if (!File.Exists(exe))
        {
            MessageBox.Show("找不到：" + exe, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (!captureOutput)
        {
            TrySaveConfigFromUi(false, false);
            string command = "/c \"\"" + exe + "\" " + args + " & echo. & pause\"";
            Process process = Process.Start(new ProcessStartInfo("cmd.exe", command) { WorkingDirectory = _root, UseShellExecute = true });
            if (process != null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += delegate
                {
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            RefreshClickPointsAfterTool();
                            RefreshStatus();
                        });
                    }
                    catch { }
                };
            }
            return;
        }

        try
        {
            Cursor.Current = Cursors.WaitCursor;
            var start = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = _root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                _log.Text = "工具：" + MonitorExe + " " + args + Environment.NewLine +
                    "退出码：" + process.ExitCode + Environment.NewLine +
                    output + (String.IsNullOrWhiteSpace(error) ? "" : Environment.NewLine + error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("运行失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }
    }

    private void RefreshClickPointsAfterTool()
    {
        if (LoadCapturedPointsToUi(false))
        {
            TrySaveConfigFromUi(false, false);
            return;
        }

        _steps = new BindingList<ClickStep>();
        _grid.DataSource = _steps;
        if (_config != null)
        {
            _config.click_steps = new List<ClickStep>();
            SaveConfig(_config);
        }
    }

    private void ClearCapturedPoints()
    {
        if (MessageBox.Show("确认清空当前已采集点击位置？会由原程序要求再次确认 CLEAR。", "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
            RunTool("--clear-captured-points");
    }

    private void OpenLogs()
    {
        string dir = Path.Combine(_root, "日志");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
    }

    private string ValidateBeforeStart()
    {
        if (!TrySaveConfigFromUi(false, false))
            return "当前界面配置无法保存，请先到“监听设置/点击设置”检查填写内容。";
        _config = LoadConfig();
        var errors = new List<string>();
        if (!File.Exists(Path.Combine(_root, MonitorExe))) errors.Add("找不到监听程序：" + MonitorExe);
        if (!HasNapCatProcess()) errors.Add("NapCat 未运行。");
        if (!CanConnect("127.0.0.1", 3001, 500)) errors.Add("NapCat WebSocket 端口 3001 不可连接。");
        if (_config.group_id <= 0) errors.Add("群号无效。");
        if ((_config.sender_qqs == null || _config.sender_qqs.Length == 0) &&
            (_config.sender_names == null || _config.sender_names.Length == 0)) errors.Add("监听 QQ 号和群名片至少填一项。");
        if (_config.keywords == null || _config.keywords.Length == 0) errors.Add("关键词为空。");
        if (_config.automation_enabled)
        {
            if (_config.click_steps == null || _config.click_steps.Count == 0) errors.Add("正式模式需要先配置点击步骤。");
            if (!HasProcess(Path.GetFileNameWithoutExtension(_config.game_process_name))) errors.Add("未找到游戏进程：" + _config.game_process_name);
            if (_config.expected_client_width <= 0 || _config.expected_client_height <= 0) errors.Add("游戏窗口尺寸未配置。");
        }
        return errors.Count == 0 ? "" : String.Join(Environment.NewLine, errors.ToArray());
    }

    private void LoadHits()
    {
        if (_hitsGrid == null) return;
        string path = Path.Combine(_root, "日志", "命中记录-" + AppVersion + ".jsonl");
        var rows = new BindingList<HitRow>();
        if (File.Exists(path))
        {
            foreach (string line in LastLines(path, 200))
            {
                try
                {
                    Dictionary<string, object> hit = _json.Deserialize<Dictionary<string, object>>(line);
                    rows.Add(new HitRow
                    {
                        时间 = Value(hit, "detected_at"),
                        群号 = Value(hit, "group_id"),
                        发送者 = Value(hit, "user_id"),
                        关键词 = Value(hit, "keyword"),
                        内容 = Value(hit, "content")
                    });
                }
                catch { }
            }
        }
        _hitsGrid.DataSource = rows;
    }

    private static IEnumerable<string> LastLines(string path, int maxLines)
    {
        var lines = new Queue<string>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs, Encoding.UTF8, true))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                lines.Enqueue(line);
                while (lines.Count > maxLines) lines.Dequeue();
            }
        }
        return lines.ToArray();
    }

    private static string Value(Dictionary<string, object> row, string key)
    {
        object value;
        return row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
    }

    private static bool HasProcess(string name)
    {
        if (String.IsNullOrWhiteSpace(name)) return false;
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)).Length > 0;
    }

    private static IntPtr FindGameWindow(string processName)
    {
        if (String.IsNullOrWhiteSpace(processName)) return IntPtr.Zero;
        Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName.Trim()));
        IntPtr result = IntPtr.Zero;
        for (int i = 0; i < processes.Length; i++)
        {
            try
            {
                if (result == IntPtr.Zero && processes[i].MainWindowHandle != IntPtr.Zero)
                    result = processes[i].MainWindowHandle;
            }
            catch { }
            finally { processes[i].Dispose(); }
        }
        return result;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static bool HasNapCatProcess()
    {
        return HasProcess("NapCatQQ-Desktop") || HasProcess("NapCatWinBootMain");
    }

    private static bool HasMonitorProcess()
    {
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.StartsWith("napcat-monitor-v", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static bool CanConnect(string host, int port, int timeoutMs)
    {
        using (var client = new TcpClient())
        {
            IAsyncResult result = client.BeginConnect(host, port, null, null);
            bool ok = result.AsyncWaitHandle.WaitOne(timeoutMs);
            if (!ok) return false;
            try { client.EndConnect(result); return true; }
            catch { return false; }
        }
    }

    private static string ReadQqId(string botJsonPath)
    {
        if (!File.Exists(botJsonPath)) return "未找到配置";
        string text = File.ReadAllText(botJsonPath, Encoding.UTF8);
        Match m = Regex.Match(text, "\"QQID\"\\s*:\\s*(\\d+)");
        return m.Success ? m.Groups[1].Value : "未配置";
    }

    private string RuntimeStateText()
    {
        string path = Path.Combine(_root, "日志", "运行状态.json");
        if (!File.Exists(path)) return "未启动/无状态文件";
        try
        {
            RuntimeStateInfo info = _json.Deserialize<RuntimeStateInfo>(File.ReadAllText(path, Encoding.UTF8));
            if (info == null || String.IsNullOrWhiteSpace(info.state)) return "未知";
            string text = info.state;
            if (!String.IsNullOrWhiteSpace(info.detail)) text += " - " + info.detail;

            DateTimeOffset until;
            if (DateTimeOffset.TryParse(info.cooldown_until, out until))
            {
                TimeSpan remain = until.ToUniversalTime() - DateTimeOffset.UtcNow;
                if (remain.TotalMilliseconds > 0)
                    text += "，冷却剩余 " + Math.Ceiling(remain.TotalSeconds) + " 秒";
            }
            return text;
        }
        catch (Exception ex)
        {
            return "读取失败：" + ex.Message;
        }
    }

    private static string BuildBotJson(string qq)
    {
        return "{\r\n" +
            "  \"bots\": [\r\n" +
            "    {\r\n" +
            "      \"bot\": {\r\n" +
            "        \"name\": \"QQ实时关键词监控\",\r\n" +
            "        \"QQID\": " + qq + ",\r\n" +
            "        \"musicSignUrl\": \"\",\r\n" +
            "        \"autoRestartSchedule\": { \"enable\": false, \"time_unit\": \"h\", \"duration\": 6 },\r\n" +
            "        \"offlineAutoRestart\": true,\r\n" +
            "        \"runtime_target\": \"local\",\r\n" +
            "        \"backend_type\": \"napcat\",\r\n" +
            "        \"deploymentType\": \"native\"\r\n" +
            "      },\r\n" +
            "      \"connect\": {\r\n" +
            "        \"httpServers\": [], \"httpSseServers\": [], \"httpClients\": [],\r\n" +
            "        \"websocketServers\": [ { \"enable\": true, \"name\": \"qq-monitor-ws\", \"messagePostFormat\": \"array\", \"token\": \"\", \"debug\": false, \"host\": \"127.0.0.1\", \"port\": 3001, \"reportSelfMessage\": false, \"enableForcePushEvent\": false, \"heartInterval\": 30000, \"path\": \"/\", \"role\": \"Universal\" } ],\r\n" +
            "        \"websocketClients\": [], \"plugins\": []\r\n" +
            "      },\r\n" +
            "      \"advanced\": {\r\n" +
            "        \"autoStart\": true, \"offlineNotice\": false, \"parseMultMsg\": false, \"packetServer\": \"\", \"packetBackend\": \"auto\", \"enableLocalFile2Url\": false,\r\n" +
            "        \"fileLog\": true, \"consoleLog\": true, \"fileLogLevel\": \"info\", \"consoleLogLevel\": \"info\", \"o3HookMode\": 1,\r\n" +
            "        \"bypass\": { \"hook\": false, \"window\": false, \"module\": false, \"process\": false, \"container\": false, \"js\": false }\r\n" +
            "      }\r\n" +
            "    }\r\n" +
            "  ],\r\n" +
            "  \"info\": { \"configVersion\": \"v2.1\" }\r\n" +
            "}\r\n";
    }

    private static string BuildOneBotJson()
    {
        return "{\r\n" +
            "  \"enableLocalFile2Url\": false,\r\n" +
            "  \"musicSignUrl\": \"\",\r\n" +
            "  \"network\": {\r\n" +
            "    \"httpClients\": [], \"httpServers\": [], \"httpSseServers\": [], \"plugins\": [], \"websocketClients\": [],\r\n" +
            "    \"websocketServers\": [ { \"enable\": true, \"enableForcePushEvent\": false, \"heartInterval\": 30000, \"host\": \"127.0.0.1\", \"messagePostFormat\": \"array\", \"name\": \"qq-monitor-ws\", \"port\": 3001, \"reportSelfMessage\": false, \"token\": \"\" } ]\r\n" +
            "  },\r\n" +
            "  \"parseMultMsg\": false\r\n" +
            "}\r\n";
    }

    private static string BuildNapCatJson()
    {
        return "{\r\n" +
            "  \"bypass\": { \"container\": false, \"hook\": false, \"js\": false, \"module\": false, \"process\": false, \"window\": false },\r\n" +
            "  \"consoleLog\": true,\r\n" +
            "  \"consoleLogLevel\": \"info\",\r\n" +
            "  \"fileLog\": true,\r\n" +
            "  \"fileLogLevel\": \"info\",\r\n" +
            "  \"o3HookMode\": 1,\r\n" +
            "  \"packetBackend\": \"auto\"\r\n" +
            "}\r\n";
    }

    private static string BuildProtocolJson()
    {
        return "{\r\n" +
            "  \"enable\": false,\r\n" +
            "  \"network\": { \"httpServers\": [], \"websocketServers\": [], \"websocketClients\": [] }\r\n" +
            "}\r\n";
    }

    private static string Tail(string path, int maxLines)
    {
        if (!File.Exists(path)) return "暂无运行日志。";
        var lines = new Queue<string>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs, Encoding.UTF8, true))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Enqueue(line);
                while (lines.Count > maxLines) lines.Dequeue();
            }
        }
        return String.Join(Environment.NewLine, lines.ToArray());
    }

    private static string YesNo(bool value)
    {
        return value ? "正常" : "未就绪";
    }

    private static decimal Clamp(int value, NumericUpDown control)
    {
        if (value < control.Minimum) return control.Minimum;
        if (value > control.Maximum) return control.Maximum;
        return value;
    }

    private static string JoinLines(IList<long> values)
    {
        if (values == null) return "";
        var parts = new List<string>();
        foreach (long value in values) parts.Add(value.ToString());
        return String.Join(Environment.NewLine, parts.ToArray());
    }

    private static long[] ParseLongs(string text)
    {
        var result = new List<long>();
        foreach (string part in Regex.Split(text ?? "", "[\\s,，;；]+"))
        {
            string item = part.Trim();
            if (item.Length == 0) continue;
            long value;
            if (!long.TryParse(item, out value) || value <= 0)
                throw new InvalidDataException("QQ号无效：" + item);
            result.Add(value);
        }
        return result.ToArray();
    }

    private static string[] ParseStrings(string text)
    {
        var result = new List<string>();
        foreach (string line in (text ?? "").Replace("，", "\n").Split(new[] { '\r', '\n', ',', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string item = line.Trim();
            if (item.Length > 0) result.Add(item);
        }
        return result.ToArray();
    }

    private static List<ClickStep> CleanSteps(IEnumerable<ClickStep> source)
    {
        var result = new List<ClickStep>();
        int index = 1;
        foreach (ClickStep s in source)
        {
            if (s == null) continue;
            if (String.IsNullOrWhiteSpace(s.name) && s.x == 0 && s.y == 0 && s.delay_ms == 0 && s.clicks == 0) continue;
            if (String.IsNullOrWhiteSpace(s.name)) s.name = "步骤" + index;
            if (s.delay_ms < 0 || s.delay_ms > 60000) throw new InvalidDataException(s.name + " 等待时间范围应为 0-60000。");
            if (s.clicks < 1 || s.clicks > 100) throw new InvalidDataException(s.name + " 点击次数范围应为 1-100。");
            if (s.interval_ms < 0 || s.interval_ms > 10000) throw new InvalidDataException(s.name + " 连点间隔范围应为 0-10000。");
            result.Add(s);
            index++;
        }
        return result;
    }

    private sealed class Config
    {
        public string napcat_ws_url { get; set; }
        public string access_token { get; set; }
        public long group_id { get; set; }
        public long[] sender_qqs { get; set; }
        public string[] sender_names { get; set; }
        public string[] keywords { get; set; }
        public string keyword_match { get; set; }
        public bool ignore_case { get; set; }
        public string callback_url { get; set; }
        public string callback_bearer_token { get; set; }
        public int callback_timeout_ms { get; set; }
        public int reconnect_seconds { get; set; }
        public int dedupe_cache_size { get; set; }
        public int? trigger_cooldown_ms { get; set; }
        public bool automation_enabled { get; set; }
        public string game_process_name { get; set; }
        public int expected_client_width { get; set; }
        public int expected_client_height { get; set; }
        public int focus_wait_ms { get; set; }
        public List<ClickStep> click_steps { get; set; }
    }

    public sealed class ClickStep
    {
        public string name { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public int delay_ms { get; set; }
        public int clicks { get; set; }
        public int interval_ms { get; set; }
    }

    public sealed class HitRow
    {
        public string 时间 { get; set; }
        public string 群号 { get; set; }
        public string 发送者 { get; set; }
        public string 关键词 { get; set; }
        public string 内容 { get; set; }
    }

    private sealed class CapturedPoints
    {
        public string game_process_name { get; set; }
        public int expected_client_width { get; set; }
        public int expected_client_height { get; set; }
        public List<ClickStep> click_steps { get; set; }
    }

    private sealed class RuntimeStateInfo
    {
        public string version { get; set; }
        public string state { get; set; }
        public string detail { get; set; }
        public string updated_at { get; set; }
        public string cooldown_until { get; set; }
        public bool automation_running { get; set; }
    }
}
