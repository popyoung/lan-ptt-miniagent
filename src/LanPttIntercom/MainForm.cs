using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LanPttIntercom.Audio;
using LanPttIntercom.Models;
using LanPttIntercom.Storage;

namespace LanPttIntercom;

/// <summary>
/// Main application window. All visible text is in Chinese (zh-Hans).
/// </summary>
public sealed class MainForm : Form
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly IntercomController _controller;
    private readonly BindingList<SavedEndpoint> _endpointsBinding;
    private readonly BindingSource _endpointsSource = new();

    private readonly Regex _ipv4Regex = new(
        @"^(25[0-5]|2[0-4]\d|[01]?\d?\d)\." +
        @"(25[0-5]|2[0-4]\d|[01]?\d?\d)\." +
        @"(25[0-5]|2[0-4]\d|[01]?\d?\d)\." +
        @"(25[0-5]|2[0-4]\d|[01]?\d?\d)$",
        RegexOptions.Compiled);

    // --- Controls ---
    private Label _lblLocalStatus = null!;
    private Label _lblLocalStatusValue = null!;
    private Label _lblTarget = null!;
    private TextBox _txtTargetIp = null!;
    private Button _btnApplyTarget = null!;
    private Button _btnLoadDefault = null!;

    private Label _lblSavedList = null!;
    private ListView _lvEndpoints = null!;
    private Button _btnAdd = null!;
    private Button _btnEdit = null!;
    private Button _btnDelete = null!;
    private Button _btnSetDefault = null!;
    private Button _btnLoadSelected = null!;

    private Button _btnPtt = null!;
    private CheckBox _chkPttKey = null!;
    private Label _lblRemoteStatus = null!;
    private Label _lblRemoteStatusValue = null!;
    private Label _lblConn = null!;
    private Label _lblConnValue = null!;
    private Label _lblLog = null!;
    private ListBox _lstLog = null!;
    private Label _lblPort = null!;
    private NumericUpDown _numPort = null!;
    private Label _lblInput = null!;
    private ComboBox _cmbInput = null!;
    private Label _lblOutput = null!;
    private ComboBox _cmbOutput = null!;
    private TableLayoutPanel _audioSettingsLayout = null!;
    private Label _lblVolume = null!;
    private TrackBar _trkVolume = null!;
    private CheckBox _chkEnhancement = null!;
    private Label _lblEnhanceStrength = null!;
    private TrackBar _trkEnhancement = null!;
    private Label _lblEnhanceValue = null!;
    private Button _btnRestartAudio = null!;
    private Button _btnShowSettings = null!;
    private Label _lblCopyright = null!;

    public MainForm()
    {
        _store = new SettingsStore();
        _settings = _store.Load();
        _controller = new IntercomController(_settings, _store);
        _endpointsBinding = new BindingList<SavedEndpoint>(_settings.Endpoints);
        _endpointsSource.DataSource = _endpointsBinding;

        InitializeUi();
        if (!string.IsNullOrEmpty(_store.LastLoadWarning))
        {
            AppendLog("[警告] " + _store.LastLoadWarning);
        }
        LoadEndpointsIntoList();
        ApplySettingsToUi();
        WireControllerEvents();
        FormClosing += OnFormClosing;
        Shown += OnFormShown;
    }

    private void OnFormShown(object? sender, EventArgs e)
    {
        // Start listening automatically on open, per the task spec. We do this
        // from Shown (not the constructor) so the window handle exists by the
        // time controller events try to update UI labels.
        SafeStartListening();
    }

    // ---------------- UI construction ----------------

    private void InitializeUi()
    {
        Text = "局域网对讲机 - 按住说话";
        Width = 760;
        Height = 780;
        MinimumSize = new Size(700, 760);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;
        DoubleBuffered = true;

        // Top row: local status + connection
        _lblLocalStatus = new Label { Text = "本机监听:", Left = 16, Top = 14, Width = 80, AutoSize = false };
        _lblLocalStatusValue = new Label
        {
            Text = "未启动",
            Left = 96,
            Top = 14,
            Width = 200,
            AutoSize = false,
            ForeColor = Color.DarkRed,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
        };

        _lblConn = new Label { Text = "连接状态:", Left = 320, Top = 14, Width = 80, AutoSize = false };
        _lblConnValue = new Label
        {
            Text = "空闲",
            Left = 400,
            Top = 14,
            Width = 320,
            AutoSize = false,
            ForeColor = Color.DimGray
        };

        // Target IP
        _lblTarget = new Label { Text = "目标 IP:", Left = 16, Top = 46, Width = 80, AutoSize = false };
        _txtTargetIp = new TextBox { Left = 96, Top = 43, Width = 220 };
        _btnApplyTarget = new Button { Text = "设为目标", Left = 322, Top = 42, Width = 90, Height = 27 };
        _btnLoadDefault = new Button { Text = "载入默认", Left = 418, Top = 42, Width = 90, Height = 27 };
        _btnShowSettings = new Button { Text = "设置文件", Left = 514, Top = 42, Width = 90, Height = 27 };

        _lblPort = new Label { Text = "端口:", Left = 610, Top = 46, Width = 40, AutoSize = false };
        _numPort = new NumericUpDown
        {
            Left = 648,
            Top = 43,
            Width = 80,
            Minimum = 1024,
            Maximum = 65535,
            Value = _settings.ListenPort
        };

        // Saved IP list
        _lblSavedList = new Label
        {
            Text = "已保存的 IP 地址(双击一行可快速设为目标):",
            Left = 16,
            Top = 80,
            Width = 720,
            AutoSize = false
        };

        _lvEndpoints = new ListView
        {
            Left = 16,
            Top = 104,
            Width = 720,
            Height = 180,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false
        };
        _lvEndpoints.Columns.Add("默认", 60);
        _lvEndpoints.Columns.Add("名称/备注", 200);
        _lvEndpoints.Columns.Add("IP 地址", 160);
        _lvEndpoints.Columns.Add("最近更新", 280);
        _lvEndpoints.DoubleClick += OnEndpointsDoubleClick;
        _lvEndpoints.SelectedIndexChanged += OnEndpointSelectedChanged;

        _btnAdd = new Button { Text = "添加", Left = 16, Top = 290, Width = 80, Height = 28 };
        _btnEdit = new Button { Text = "编辑", Left = 102, Top = 290, Width = 80, Height = 28 };
        _btnDelete = new Button { Text = "删除", Left = 188, Top = 290, Width = 80, Height = 28 };
        _btnSetDefault = new Button { Text = "设为默认", Left = 274, Top = 290, Width = 90, Height = 28 };
        _btnLoadSelected = new Button { Text = "载入为目标", Left = 370, Top = 290, Width = 100, Height = 28 };

        // PTT
        _btnPtt = new Button
        {
            Text = "按住说话(也可以按住 空格 键)",
            Left = 16,
            Top = 332,
            Width = 480,
            Height = 60,
            BackColor = Color.FromArgb(0xE6, 0x4A, 0x4A),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point)
        };
        _btnPtt.MouseDown += (_, __) => BeginPtt();
        _btnPtt.MouseUp += (_, __) => EndPtt();
        _btnPtt.MouseLeave += (_, __) => { if (_controller.IsTransmitting) EndPtt(); };
        _btnPtt.KeyDown += OnPttKeyDown;

        _chkPttKey = new CheckBox
        {
            Text = "启用 空格键 作为按住说话",
            Left = 510,
            Top = 340,
            Width = 230,
            Height = 24,
            Checked = _settings.Ui.PttKeyEnabled
        };
        _chkPttKey.CheckedChanged += (_, __) =>
        {
            _settings.Ui.PttKeyEnabled = _chkPttKey.Checked;
            TrySaveSettings();
        };

        _lblRemoteStatus = new Label { Text = "远端状态:", Left = 510, Top = 372, Width = 80, AutoSize = false };
        _lblRemoteStatusValue = new Label
        {
            Text = "空闲",
            Left = 590,
            Top = 372,
            Width = 150,
            AutoSize = false,
            ForeColor = Color.DimGray
        };

        // Audio settings are grouped in a table so DPI/font scaling cannot
        // make the TrackBar rows overlap each other.
        _audioSettingsLayout = new TableLayoutPanel
        {
            Left = 16,
            Top = 402,
            Width = 720,
            Height = 130,
            ColumnCount = 6,
            RowCount = 3,
            AutoSize = false,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _audioSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F));
        _audioSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
        _audioSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F));
        _audioSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
        _audioSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12F));
        _audioSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
        _audioSettingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        _audioSettingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        _audioSettingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

        _lblInput = new Label { Text = "麦克风:", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
        _cmbInput = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 14, 4) };
        _lblOutput = new Label { Text = "扬声器:", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
        _cmbOutput = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 8, 4) };
        _btnRestartAudio = new Button { Text = "应用音频设置", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 5) };

        _lblVolume = new Label { Text = "音量:", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
        _trkVolume = new TrackBar
        {
            Dock = DockStyle.Fill,
            Height = 36,
            AutoSize = false,
            Margin = new Padding(0, 2, 0, 2),
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            Value = _settings.Ui.OutputVolume
        };

        _chkEnhancement = new CheckBox
        {
            Text = "启用麦克风语音增强",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Checked = _settings.Audio.Enhancement.Enabled
        };
        _lblEnhanceStrength = new Label { Text = "强度:", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
        _trkEnhancement = new TrackBar
        {
            Dock = DockStyle.Fill,
            Height = 36,
            AutoSize = false,
            Margin = new Padding(0, 2, 0, 2),
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 25,
            Value = Math.Clamp(_settings.Audio.Enhancement.Strength, 0, 100)
        };
        _lblEnhanceValue = new Label
        {
            Text = _trkEnhancement.Value.ToString(),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };
        _audioSettingsLayout.Controls.Add(_lblInput, 0, 0);
        _audioSettingsLayout.Controls.Add(_cmbInput, 1, 0);
        _audioSettingsLayout.Controls.Add(_lblOutput, 2, 0);
        _audioSettingsLayout.Controls.Add(_cmbOutput, 3, 0);
        _audioSettingsLayout.Controls.Add(_btnRestartAudio, 5, 0);
        _audioSettingsLayout.Controls.Add(_lblVolume, 0, 1);
        _audioSettingsLayout.Controls.Add(_trkVolume, 1, 1);
        _audioSettingsLayout.SetColumnSpan(_trkVolume, 5);
        _audioSettingsLayout.Controls.Add(_chkEnhancement, 0, 2);
        _audioSettingsLayout.SetColumnSpan(_chkEnhancement, 2);
        _audioSettingsLayout.Controls.Add(_lblEnhanceStrength, 2, 2);
        _audioSettingsLayout.Controls.Add(_trkEnhancement, 3, 2);
        _audioSettingsLayout.SetColumnSpan(_trkEnhancement, 2);
        _audioSettingsLayout.Controls.Add(_lblEnhanceValue, 5, 2);

        // Log
        _lblLog = new Label { Text = "状态日志:", Left = 16, Top = 542, Width = 100, AutoSize = false };
        _lstLog = new ListBox
        {
            Left = 16,
            Top = 564,
            Width = 720,
            Height = 92,
            IntegralHeight = false,
            HorizontalScrollbar = true
        };

        _lblCopyright = new Label
        {
            Text = "局域网对讲机 v1.0  ·  本程序仅在同一局域网内通信",
            Left = 16,
            Top = 668,
            Width = 720,
            AutoSize = false,
            ForeColor = Color.Gray
        };

        Controls.AddRange(new Control[]
        {
            _lblLocalStatus, _lblLocalStatusValue,
            _lblConn, _lblConnValue,
            _lblTarget, _txtTargetIp, _btnApplyTarget, _btnLoadDefault, _btnShowSettings, _lblPort, _numPort,
            _lblSavedList, _lvEndpoints,
            _btnAdd, _btnEdit, _btnDelete, _btnSetDefault, _btnLoadSelected,
            _btnPtt, _chkPttKey, _lblRemoteStatus, _lblRemoteStatusValue,
            _audioSettingsLayout,
            _lblLog, _lstLog,
            _lblCopyright
        });

        // Wire button events
        _btnApplyTarget.Click += (_, __) => ApplyTargetFromTextBox();
        _btnLoadDefault.Click += (_, __) => LoadDefaultEndpoint();
        _btnAdd.Click += (_, __) => AddOrEditEndpoint(null);
        _btnEdit.Click += (_, __) =>
        {
            if (_lvEndpoints.SelectedItems.Count > 0)
            {
                AddOrEditEndpoint((SavedEndpoint)_lvEndpoints.SelectedItems[0].Tag!);
            }
        };
        _btnDelete.Click += (_, __) => DeleteSelectedEndpoint();
        _btnSetDefault.Click += (_, __) => SetDefaultForSelected();
        _btnLoadSelected.Click += (_, __) => LoadSelectedAsTarget();
        _btnShowSettings.Click += (_, __) => ShowSettingsFile();
        _btnRestartAudio.Click += (_, __) => ApplyAudioSettingsFromUi();
        _trkVolume.Scroll += (_, __) =>
        {
            _settings.Ui.OutputVolume = _trkVolume.Value;
            TrySaveSettings();
        };
        _numPort.ValueChanged += (_, __) =>
        {
            _settings.ListenPort = (int)_numPort.Value;
            TrySaveSettings();
        };
        _chkEnhancement.CheckedChanged += (_, __) =>
        {
            _settings.Audio.Enhancement.Enabled = _chkEnhancement.Checked;
            _controller.ResetVoiceEnhancer();
            UpdateEnhancementControls();
            TrySaveSettings();
        };
        _trkEnhancement.Scroll += (_, __) =>
        {
            _settings.Audio.Enhancement.Strength = _trkEnhancement.Value;
            _controller.ResetVoiceEnhancer();
            UpdateEnhancementControls();
            TrySaveSettings();
        };

        // Keyboard handling: Space = PTT when chkPttKey is checked.
        KeyDown += OnFormKeyDown;
        KeyUp += OnFormKeyUp;
    }

    private void WireControllerEvents()
    {
        _controller.StatusChanged += msg => AppendLog(msg);
        _controller.ErrorOccurred += msg => AppendLog("[错误] " + msg);
        _controller.TransmitStateChanged += tx =>
        {
            RunOnUi(() =>
            {
                if (tx)
                {
                    _btnPtt.BackColor = Color.FromArgb(0xB0, 0x30, 0x30);
                    _btnPtt.Text = "正在发送... 松开停止";
                    _lblConnValue.Text = "正在向 " + (_controller.CurrentTargetIp ?? "?") + " 发送";
                    _lblConnValue.ForeColor = Color.DarkRed;
                }
                else
                {
                    _btnPtt.BackColor = Color.FromArgb(0xE6, 0x4A, 0x4A);
                    _btnPtt.Text = "按住说话(也可以按住 空格 键)";
                    _lblConnValue.Text = "空闲 - 等待按下说话";
                    _lblConnValue.ForeColor = Color.DimGray;
                }
            });
        };
        _controller.RemotePttStateChanged += pressing =>
        {
            RunOnUi(() =>
            {
                if (pressing)
                {
                    _lblRemoteStatusValue.Text = "正在说话";
                    _lblRemoteStatusValue.ForeColor = Color.DarkGreen;
                }
                else
                {
                    _lblRemoteStatusValue.Text = "空闲";
                    _lblRemoteStatusValue.ForeColor = Color.DimGray;
                }
            });
        };
        _controller.RemoteAudioActiveChanged += ts =>
        {
            RunOnUi(() =>
            {
                _lblConnValue.Text = "正在接收远端音频";
                _lblConnValue.ForeColor = Color.DarkGreen;
            });
        };
    }

    // ---------------- Lifecycle ----------------

    private void SafeStartListening()
    {
        try
        {
            PopulateDevicePickers();
            _controller.StartListening();
            _lblLocalStatusValue.Text = "监听中 (端口 " + _settings.ListenPort + ")";
            _lblLocalStatusValue.ForeColor = Color.DarkGreen;
            // Auto-load default endpoint IP into the textbox (but DO NOT start transmitting).
            LoadDefaultEndpoint(silent: true);
        }
        catch (Exception ex)
        {
            _lblLocalStatusValue.Text = "启动失败";
            _lblLocalStatusValue.ForeColor = Color.DarkRed;
            AppendLog("[错误] 启动失败:" + ex.Message);
        }
    }

    private void OnFormClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_controller.IsTransmitting) _controller.StopTransmit();
            _controller.StopListening();
        }
        catch { /* ignore */ }
    }

    // ---------------- Helpers ----------------

    private void PopulateDevicePickers()
    {
        try
        {
            _cmbInput.Items.Clear();
            foreach (var d in IntercomController.ListInputDevices())
            {
                _cmbInput.Items.Add(d);
            }
            _cmbInput.DisplayMember = nameof(MmsDeviceInfo.Name);
            int idx = -1;
            for (int i = 0; i < _cmbInput.Items.Count; i++)
            {
                if (((MmsDeviceInfo)_cmbInput.Items[i]).DeviceId == _settings.Audio.InputDeviceId) { idx = i; break; }
            }
            _cmbInput.SelectedIndex = idx >= 0 ? idx : (_cmbInput.Items.Count > 0 ? 0 : -1);
        }
        catch (Exception ex)
        {
            AppendLog("[警告] 枚举输入设备失败:" + ex.Message);
        }
        try
        {
            _cmbOutput.Items.Clear();
            foreach (var d in IntercomController.ListOutputDevices())
            {
                _cmbOutput.Items.Add(d);
            }
            _cmbOutput.DisplayMember = nameof(MmsDeviceInfo.Name);
            int idx = -1;
            for (int i = 0; i < _cmbOutput.Items.Count; i++)
            {
                if (((MmsDeviceInfo)_cmbOutput.Items[i]).DeviceId == _settings.Audio.OutputDeviceId) { idx = i; break; }
            }
            _cmbOutput.SelectedIndex = idx >= 0 ? idx : (_cmbOutput.Items.Count > 0 ? 0 : -1);
        }
        catch (Exception ex)
        {
            AppendLog("[警告] 枚举输出设备失败:" + ex.Message);
        }
    }

    private void ApplyAudioSettingsFromUi()
    {
        if (_cmbInput.SelectedItem is MmsDeviceInfo inDev) _settings.Audio.InputDeviceId = inDev.DeviceId;
        if (_cmbOutput.SelectedItem is MmsDeviceInfo outDev) _settings.Audio.OutputDeviceId = outDev.DeviceId;
        _settings.Ui.OutputVolume = _trkVolume.Value;
        _settings.Audio.Enhancement.Enabled = _chkEnhancement.Checked;
        _settings.Audio.Enhancement.Strength = _trkEnhancement.Value;
        if (!TrySaveSettings()) return;
        _controller.ResetVoiceEnhancer();

        try
        {
            bool wasListening = _controller.IsListening;
            string? target = _controller.CurrentTargetIp;
            _controller.StopListening();
            if (wasListening) _controller.StartListening();
            if (!string.IsNullOrEmpty(target))
            {
                _controller.SetTarget(target);
            }
            AppendLog("音频设置已应用");
        }
        catch (Exception ex)
        {
            AppendLog("[错误] 重新启动音频失败:" + ex.Message);
        }
    }

    private void ApplyTargetFromTextBox()
    {
        var text = _txtTargetIp.Text.Trim();
        if (!_ipv4Regex.IsMatch(text))
        {
            MessageBox.Show(this, "目标 IP 地址格式不正确,请输入形如 192.168.1.20 的 IPv4 地址。", "格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _controller.SetTarget(text);
        AppendLog("目标已设为 " + text);
        _lblConnValue.Text = "目标 " + text + " (等待按下说话)";
        _lblConnValue.ForeColor = Color.DarkBlue;
    }

    private void LoadDefaultEndpoint(bool silent = false)
    {
        if (string.IsNullOrEmpty(_settings.DefaultEndpointId))
        {
            if (!silent) MessageBox.Show(this, "尚未设置默认 IP 地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ep = _endpointsBinding.FirstOrDefault(e => e.Id == _settings.DefaultEndpointId);
        if (ep == null)
        {
            if (!silent) MessageBox.Show(this, "默认地址已不存在,请重新选择。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _txtTargetIp.Text = ep.IpAddress;
        // We set the target too so a saved default is usable immediately, but
        // audio is still only sent when the user actually presses PTT.
        _controller.SetTarget(ep.IpAddress);
        if (!silent) AppendLog("已载入默认地址 " + ep.IpAddress);
    }

    private void LoadSelectedAsTarget()
    {
        if (_lvEndpoints.SelectedItems.Count == 0) return;
        var ep = (SavedEndpoint)_lvEndpoints.SelectedItems[0].Tag!;
        _txtTargetIp.Text = ep.IpAddress;
        _controller.SetTarget(ep.IpAddress);
        AppendLog("已将 " + ep.IpAddress + " 载入为当前目标");
    }

    private void SetDefaultForSelected()
    {
        if (_lvEndpoints.SelectedItems.Count == 0) return;
        var ep = (SavedEndpoint)_lvEndpoints.SelectedItems[0].Tag!;
        _settings.DefaultEndpointId = ep.Id;
        if (!TrySaveSettings()) return;
        LoadEndpointsIntoList();
        AppendLog("已将 " + ep.IpAddress + " 设为默认地址");
    }

    private void OnEndpointSelectedChanged(object? sender, EventArgs e)
    {
        bool has = _lvEndpoints.SelectedItems.Count > 0;
        _btnEdit.Enabled = has;
        _btnDelete.Enabled = has;
        _btnSetDefault.Enabled = has;
        _btnLoadSelected.Enabled = has;
    }

    private void OnEndpointsDoubleClick(object? sender, EventArgs e)
    {
        LoadSelectedAsTarget();
    }

    private void AddOrEditEndpoint(SavedEndpoint? existing)
    {
        using var dlg = new EndpointEditDialog(existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var result = dlg.Result;
        if (result == null) return;
        if (!_ipv4Regex.IsMatch(result.IpAddress))
        {
            MessageBox.Show(this, "IP 地址格式不正确。", "格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (existing == null)
        {
            result.Id = Guid.NewGuid().ToString("N");
            result.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            _endpointsBinding.Add(result);
        }
        else
        {
            existing.Name = result.Name;
            existing.IpAddress = result.IpAddress;
            existing.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            // Replace the entry in the settings list (BindingList updates the row).
            int idx = _settings.Endpoints.FindIndex(x => x.Id == existing.Id);
            if (idx >= 0) _settings.Endpoints[idx] = existing;
        }
        if (!TrySaveSettings()) return;
        LoadEndpointsIntoList();
        AppendLog(existing == null ? "已添加 " + result.IpAddress : "已更新 " + result.IpAddress);
    }

    private void DeleteSelectedEndpoint()
    {
        if (_lvEndpoints.SelectedItems.Count == 0) return;
        var ep = (SavedEndpoint)_lvEndpoints.SelectedItems[0].Tag!;
        var confirm = MessageBox.Show(this,
            "确定要删除 " + (string.IsNullOrEmpty(ep.Name) ? ep.IpAddress : ep.Name + " (" + ep.IpAddress + ")") + " 吗?",
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;
        if (_settings.DefaultEndpointId == ep.Id) _settings.DefaultEndpointId = null;
        _endpointsBinding.Remove(ep);
        if (!TrySaveSettings()) return;
        LoadEndpointsIntoList();
        AppendLog("已删除 " + ep.IpAddress);
    }

    private void LoadEndpointsIntoList()
    {
        _lvEndpoints.Items.Clear();
        foreach (var ep in _endpointsBinding)
        {
            var item = new ListViewItem(ep.Id == _settings.DefaultEndpointId ? "●" : "");
            item.SubItems.Add(string.IsNullOrEmpty(ep.Name) ? "(无名称)" : ep.Name);
            item.SubItems.Add(ep.IpAddress);
            item.SubItems.Add(new DateTime(ep.UpdatedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.Tag = ep;
            _lvEndpoints.Items.Add(item);
        }
        OnEndpointSelectedChanged(null, EventArgs.Empty);
    }

    private void ApplySettingsToUi()
    {
        _numPort.Value = Math.Clamp(_settings.ListenPort, (int)_numPort.Minimum, (int)_numPort.Maximum);
        _trkVolume.Value = Math.Clamp(_settings.Ui.OutputVolume, _trkVolume.Minimum, _trkVolume.Maximum);
        _chkPttKey.Checked = _settings.Ui.PttKeyEnabled;
        _chkEnhancement.Checked = _settings.Audio.Enhancement.Enabled;
        _trkEnhancement.Value = Math.Clamp(_settings.Audio.Enhancement.Strength, _trkEnhancement.Minimum, _trkEnhancement.Maximum);
        UpdateEnhancementControls();
        if (!string.IsNullOrEmpty(_settings.DefaultEndpointId))
        {
            var ep = _endpointsBinding.FirstOrDefault(e => e.Id == _settings.DefaultEndpointId);
            if (ep != null) _txtTargetIp.Text = ep.IpAddress;
        }
    }

    private void ShowSettingsFile()
    {
        try
        {
            var path = _store.FilePath;
            if (!File.Exists(path))
            {
                if (!TrySaveSettings()) return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开设置文件:" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateEnhancementControls()
    {
        _trkEnhancement.Enabled = _chkEnhancement.Checked;
        _lblEnhanceStrength.Enabled = _chkEnhancement.Checked;
        _lblEnhanceValue.Text = _trkEnhancement.Value.ToString();
        _lblEnhanceValue.ForeColor = _chkEnhancement.Checked ? Color.DarkBlue : Color.DimGray;
    }

    private bool TrySaveSettings()
    {
        try
        {
            _store.Save(_settings);
            return true;
        }
        catch (Exception ex)
        {
            var message = "保存设置失败:" + ex.Message + "。设置目录:" + _store.BaseDirectory;
            PortableRuntimeLog.Write(_store.BaseDirectory, message);
            AppendLog("[错误] " + message);
            MessageBox.Show(this, message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    // ---------------- PTT ----------------

    private void BeginPtt()
    {
        if (string.IsNullOrEmpty(_controller.CurrentTargetIp))
        {
            AppendLog("[提示] 请先设置目标 IP");
            // Pop the textbox value if it looks valid.
            if (_ipv4Regex.IsMatch(_txtTargetIp.Text.Trim()))
            {
                _controller.SetTarget(_txtTargetIp.Text.Trim());
            }
            else
            {
                return;
            }
        }
        _controller.StartTransmit();
    }

    private void EndPtt()
    {
        if (_controller.IsTransmitting) _controller.StopTransmit();
    }

    private bool _spaceDown;

    /// <summary>
    /// Returns true when the currently focused control is a text-editing control
    /// where the user is likely typing (TextBox, NumericUpDown, editable ComboBox).
    /// We must NOT trigger PTT on space when this is the case, otherwise typing
    /// "192.168.1.1 " in the IP box would call out.
    /// </summary>
    private bool IsTextInputFocused()
    {
        var f = FocusedControl();
        if (f == null) return false;
        if (f is TextBox) return true;
        if (f is NumericUpDown) return true;
        if (f is ComboBox cb && cb.DropDownStyle == ComboBoxStyle.DropDown) return true;
        return false;
    }

    /// <summary>
    /// Returns the leaf control that currently has keyboard focus, walking down
    /// any container chain. Returns null if nothing has focus.
    /// </summary>
    private Control? FocusedControl()
    {
        var f = ActiveControl;
        while (f is ContainerControl cc && cc.ActiveControl != null && cc.ActiveControl != f)
        {
            f = cc.ActiveControl;
        }
        return f;
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && _settings.Ui.PttKeyEnabled && !_spaceDown && !IsTextInputFocused())
        {
            _spaceDown = true;
            BeginPtt();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnFormKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && _spaceDown)
        {
            _spaceDown = false;
            EndPtt();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnPttKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) e.Handled = true;
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        action();
                    }
                }));
            }
            catch (ObjectDisposedException)
            {
                // Shutdown raced the posted event.
            }
            catch (InvalidOperationException)
            {
                // The form handle can disappear while a background event is being posted.
            }
            return;
        }

        action();
    }

    private void AppendLog(string text)
    {
        RunOnUi(() =>
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text;
            _lstLog.Items.Insert(0, line);
            while (_lstLog.Items.Count > 200) _lstLog.Items.RemoveAt(_lstLog.Items.Count - 1);
        });
    }
}

/// <summary>
/// Simple modal dialog used to add or edit a saved endpoint.
/// </summary>
internal sealed class EndpointEditDialog : Form
{
    private readonly TextBox _txtName = null!;
    private readonly TextBox _txtIp = null!;
    private readonly Button _btnOk = null!;
    private readonly Button _btnCancel = null!;
    private readonly Label _lblName = null!;
    private readonly Label _lblIp = null!;

    public SavedEndpoint? Result { get; private set; }

    public EndpointEditDialog(SavedEndpoint? existing)
    {
        Text = existing == null ? "添加 IP 地址" : "编辑 IP 地址";
        Width = 360;
        Height = 200;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        _lblName = new Label { Text = "名称/备注:", Left = 16, Top = 18, Width = 90, AutoSize = false };
        _txtName = new TextBox { Left = 110, Top = 15, Width = 220 };
        _lblIp = new Label { Text = "IP 地址:", Left = 16, Top = 58, Width = 90, AutoSize = false };
        _txtIp = new TextBox { Left = 110, Top = 55, Width = 220 };
        _btnOk = new Button { Text = "确定", Left = 110, Top = 110, Width = 90, Height = 30, DialogResult = DialogResult.OK };
        _btnCancel = new Button { Text = "取消", Left = 220, Top = 110, Width = 90, Height = 30, DialogResult = DialogResult.Cancel };

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        Controls.AddRange(new Control[] { _lblName, _txtName, _lblIp, _txtIp, _btnOk, _btnCancel });
        if (existing != null)
        {
            _txtName.Text = existing.Name;
            _txtIp.Text = existing.IpAddress;
        }

        _btnOk.Click += (_, __) =>
        {
            var ip = _txtIp.Text.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show(this, "请输入 IP 地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            Result = new SavedEndpoint
            {
                Name = _txtName.Text.Trim(),
                IpAddress = ip
            };
        };
    }
}
