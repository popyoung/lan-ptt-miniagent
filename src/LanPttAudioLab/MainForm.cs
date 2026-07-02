using System.Diagnostics;
using System.Text;
using LanPttIntercom.Audio;
using LanPttIntercom.Models;

namespace LanPttAudioLab;

public sealed class MainForm : Form
{
    private const string SpeechPrompt =
        "speech-quality 固定脚本:\r\n" +
        "1. 请保持安静 2 秒，用于采集底噪。\r\n" +
        "2. 用正常音量说一句常用测试句，内容为：侬则钟桑。然后停顿 1~3 秒。\r\n" +
        "3. 用较小音量说同一句话。然后停顿 1~3 秒。\r\n" +
        "4. 用较大音量说同一句话。然后停顿 1~3 秒。\r\n" +
        "5. 说一些容易产生爆破音的词，词与词之间停顿 1~3 秒：怕痛，特别，铁血，等到，地皮，对付，跑步，破坏，肥胖，墙壁，笨蛋，疾病。\r\n" +
        "6. 说一句带有爆破音的句子。中间故意停顿，用于检查断续、门限和 ducking。句子内容为：外面发生了爆炸，你快逃。（此处停顿 1~3 秒）跑步前进！\r\n" +
        "7. 最后再用正常音量说一句话，用于检查爆破音或大声段之后是否恢复正常。内容为：侬则钟桑。";

    private const string PitchPrompt =
        "pitch-sweep 提示:\r\n" +
        "请录制任意数量的音高段。\r\n" +
        "每段尽量保持一个音高 1-3 秒，然后停顿一下。\r\n" +
        "下一段换一个不同音高，再保持 1-3 秒。\r\n" +
        "不要求唱准固定音名，也不要求按低/中/高顺序录制。";

    private readonly TextBox _txtDirectory = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _cmbRunType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _cmbInputDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _numFrameMilliseconds = new() { Minimum = 10, Maximum = 100, Value = 20, Dock = DockStyle.Left, Width = 80 };
    private readonly TextBox _txtPrompt = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox _txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button _btnRecord = new() { Text = "开始录制 raw.wav", AutoSize = true };
    private readonly Button _btnRun = new() { Text = "复用 raw.wav 并运行全部 presets", AutoSize = true };
    private MmsAudioCapture? _capture;
    private readonly List<byte> _captured = new();
    private int _recordingSampleRate = 16000;
    private bool _isRecording;
    private bool _frameMillisecondsEdited;
    private bool _syncingFrameMilliseconds;

    public MainForm()
    {
        Text = "LanPttAudioLab";
        Width = 980;
        Height = 760;

        _cmbRunType.Items.Add(AudioLabRunner.SpeechQuality);
        _cmbRunType.Items.Add(AudioLabRunner.PitchSweep);
        _cmbRunType.SelectedIndex = 0;
        _cmbRunType.SelectedIndexChanged += (_, __) => UpdatePrompt();
        _numFrameMilliseconds.ValueChanged += (_, __) =>
        {
            if (!_syncingFrameMilliseconds)
            {
                _frameMillisecondsEdited = true;
            }
        };

        _txtDirectory.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioLabRuns", DateTime.Now.ToString("yyyy-MM-dd-speech-001"));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        AddLabel(layout, "实验目录", 0);
        layout.Controls.Add(_txtDirectory, 1, 0);
        var browse = new Button { Text = "选择/创建...", Dock = DockStyle.Fill };
        browse.Click += (_, __) => BrowseDirectory();
        layout.Controls.Add(browse, 2, 0);

        AddLabel(layout, "run type", 1);
        layout.Controls.Add(_cmbRunType, 1, 1);
        var createPresets = new Button { Text = "加载/创建 presets", Dock = DockStyle.Fill };
        createPresets.Click += (_, __) => CreatePresets();
        layout.Controls.Add(createPresets, 2, 1);

        AddLabel(layout, "输入设备", 2);
        layout.Controls.Add(_cmbInputDevice, 1, 2);
        var refreshDevices = new Button { Text = "刷新设备", Dock = DockStyle.Fill };
        refreshDevices.Click += (_, __) => LoadDevices();
        layout.Controls.Add(refreshDevices, 2, 2);

        AddLabel(layout, "帧长 ms", 3);
        layout.Controls.Add(_numFrameMilliseconds, 1, 3);

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        buttonPanel.Controls.Add(_btnRecord);
        buttonPanel.Controls.Add(_btnRun);
        var openDir = new Button { Text = "打开目录", AutoSize = true };
        openDir.Click += (_, __) => OpenPath(ExperimentDirectory);
        var openReport = new Button { Text = "打开 report.html", AutoSize = true };
        openReport.Click += (_, __) => OpenPath(Path.Combine(ExperimentDirectory, "report.html"));
        buttonPanel.Controls.Add(openDir);
        buttonPanel.Controls.Add(openReport);
        layout.Controls.Add(buttonPanel, 1, 4);
        layout.SetColumnSpan(buttonPanel, 2);

        AddLabel(layout, "录制提示", 5);
        layout.Controls.Add(_txtPrompt, 1, 5);
        layout.SetColumnSpan(_txtPrompt, 2);

        AddLabel(layout, "日志", 6);
        layout.Controls.Add(_txtLog, 1, 6);
        layout.SetColumnSpan(_txtLog, 2);

        Controls.Add(layout);
        _btnRecord.Click += (_, __) => ToggleRecording();
        _btnRun.Click += (_, __) => RunPresets();

        LoadDevices();
        UpdatePrompt();
    }

    private string ExperimentDirectory => Path.GetFullPath(_txtDirectory.Text.Trim());

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(_txtDirectory.Text) ? _txtDirectory.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _txtDirectory.Text = dialog.SelectedPath;
        }
    }

    private void LoadDevices()
    {
        _cmbInputDevice.Items.Clear();
        _cmbInputDevice.Items.Add(new MmsDeviceInfo(-1, "系统默认"));
        foreach (var device in MmsAudioCapture.EnumerateInputDevices())
        {
            _cmbInputDevice.Items.Add(device);
        }
        _cmbInputDevice.SelectedIndex = 0;
    }

    private void CreatePresets()
    {
        try
        {
            Directory.CreateDirectory(ExperimentDirectory);
            var path = Path.Combine(ExperimentDirectory, "lab-presets.json");
            var set = AudioLabPresetSet.LoadOrCreate(path);
            ApplyRecordingSettingsToUi(set.Recording);
            Log("已加载/创建 " + path);
        }
        catch (Exception ex)
        {
            ShowError("加载/创建 lab-presets.json 失败", ex);
        }
    }

    private void ApplyRecordingSettingsToUi(AudioLabRecordingSettings recording)
    {
        _syncingFrameMilliseconds = true;
        try
        {
            _numFrameMilliseconds.Value = Math.Clamp(recording.FrameMilliseconds, (int)_numFrameMilliseconds.Minimum, (int)_numFrameMilliseconds.Maximum);
        }
        finally
        {
            _syncingFrameMilliseconds = false;
        }

        _frameMillisecondsEdited = false;
    }

    private void ToggleRecording()
    {
        if (_isRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    private void StartRecording()
    {
        try
        {
            Directory.CreateDirectory(ExperimentDirectory);
            var presetPath = Path.Combine(ExperimentDirectory, "lab-presets.json");
            var set = AudioLabPresetSet.LoadOrCreate(presetPath);
            if (!_frameMillisecondsEdited)
            {
                ApplyRecordingSettingsToUi(set.Recording);
            }

            set.Recording.FrameMilliseconds = set.Recording.ResolveFrameMillisecondsForCapture((int)_numFrameMilliseconds.Value, _frameMillisecondsEdited);
            set.Recording.InputDeviceId = SelectedInputDeviceId();
            set.Save(presetPath);
            _frameMillisecondsEdited = false;

            var settings = set.Recording.ToAudioSettings(strength: 50, maxGainMultiplier: 8);
            settings.Enhancement.Enabled = false;
            settings.InputDeviceId = set.Recording.InputDeviceId;

            _recordingSampleRate = settings.SampleRate;
            _captured.Clear();
            _capture = new MmsAudioCapture(settings);
            _capture.FrameCaptured += frame =>
            {
                lock (_captured)
                {
                    _captured.AddRange(frame);
                }
            };
            _capture.ErrorOccurred += message =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(() => Log("录音错误: " + message)));
                }
            };
            _capture.Start();
            _isRecording = true;
            _btnRecord.Text = "停止录制并保存 raw.wav";
            _btnRun.Enabled = false;
            Log("开始录制 raw.wav；再次点击录制按钮停止并保存。");
        }
        catch (Exception ex)
        {
            _capture?.Dispose();
            _capture = null;
            _isRecording = false;
            _btnRecord.Text = "开始录制 raw.wav";
            _btnRun.Enabled = true;
            ShowError("录制 raw.wav 失败", ex);
        }
    }

    private void StopRecording()
    {
        try
        {
            _capture?.Dispose();

            byte[] bytes;
            lock (_captured)
            {
                bytes = _captured.ToArray();
            }

            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("没有采集到音频数据，raw.wav 未写出。");
            }

            var samples = WavFile.BytesToSamples(bytes);
            var path = Path.Combine(ExperimentDirectory, "raw.wav");
            WavFile.WritePcm16Mono(path, _recordingSampleRate, samples);
            Log("已写出 " + path + "，样本数: " + samples.Length);
        }
        catch (Exception ex)
        {
            ShowError("保存 raw.wav 失败", ex);
        }
        finally
        {
            _capture = null;
            _isRecording = false;
            _btnRecord.Text = "开始录制 raw.wav";
            _btnRun.Enabled = true;
        }
    }

    private void RunPresets()
    {
        try
        {
            var result = AudioLabRunner.RunAllPresets(ExperimentDirectory, SelectedRunType());
            Log("运行完成: " + result.ReportPath);
        }
        catch (Exception ex)
        {
            ShowError("运行 presets 或生成报告失败", ex);
        }
    }

    private int SelectedInputDeviceId()
    {
        return _cmbInputDevice.SelectedItem is MmsDeviceInfo info ? info.DeviceId : -1;
    }

    private string SelectedRunType()
    {
        return _cmbRunType.SelectedItem?.ToString() == AudioLabRunner.PitchSweep
            ? AudioLabRunner.PitchSweep
            : AudioLabRunner.SpeechQuality;
    }

    private void UpdatePrompt()
    {
        _txtPrompt.Text = SelectedRunType() == AudioLabRunner.PitchSweep ? PitchPrompt : SpeechPrompt;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isRecording)
        {
            Log("窗口关闭，正在停止录制并保存 raw.wav。");
            StopRecording();
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _capture?.Dispose();
        base.OnFormClosed(e);
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                throw new FileNotFoundException("路径不存在", path);
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("打开路径失败", ex);
        }
    }

    private static void AddLabel(TableLayoutPanel layout, string text, int row)
    {
        layout.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
    }

    private void Log(string message)
    {
        _txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
    }

    private void ShowError(string title, Exception ex)
    {
        Log(title + ": " + ex.Message);
        MessageBox.Show(this, title + "\r\n\r\n" + ex.Message, "LanPttAudioLab", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
