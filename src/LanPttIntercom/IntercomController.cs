using System;
using System.Collections.Generic;
using LanPttIntercom.Audio;
using LanPttIntercom.Models;
using LanPttIntercom.Network;
using LanPttIntercom.Storage;

namespace LanPttIntercom;

/// <summary>
/// Coordinates audio capture, audio playback, and the UDP transport. Holds the
/// runtime state shared by the UI form: current target, transmit state, and
/// remote PTT state.
/// </summary>
public sealed class IntercomController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly VoiceUdpClient _udp;
    private readonly TransmitStateGate _transmitState = new();
    private readonly object _voiceEnhancerLock = new();
    private MmsAudioCapture? _capture;
    private MmsAudioPlayback? _playback;
    private VoiceEnhancer? _voiceEnhancer;
    private bool _remoteIsPressing;
    private DateTime _lastRemoteAudioAt = DateTime.MinValue;

    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<bool>? TransmitStateChanged;        // local PTT
    public event Action<bool>? RemotePttStateChanged;       // remote PTT
    public event Action<DateTime>? RemoteAudioActiveChanged; // last frame timestamp

    public bool IsListening { get; private set; }
    public bool IsTransmitting => _transmitState.IsTransmitting;
    public bool RemoteIsPressing => _remoteIsPressing;
    public DateTime LastRemoteAudioAt => _lastRemoteAudioAt;
    public string? CurrentTargetIp { get; private set; }
    public AppSettings Settings => _settings;

    public IntercomController(AppSettings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
        _udp = new VoiceUdpClient(_settings.ListenPort);
        _udp.AudioFrameReceived += OnRemoteAudio;
        _udp.RemotePttStateChanged += OnRemotePtt;
        _udp.ErrorOccurred += msg => ErrorOccurred?.Invoke(msg);
    }

    public static IReadOnlyList<MmsDeviceInfo> ListInputDevices() => MmsAudioCapture.EnumerateInputDevices();
    public static IReadOnlyList<MmsDeviceInfo> ListOutputDevices() => MmsAudioPlayback.EnumerateOutputDevices();

    /// <summary>Open the UDP socket and start capture + playback. Idempotent.</summary>
    public void StartListening()
    {
        if (IsListening) return;

        _udp.StartListening();
        var captureStarted = false;
        var playbackStarted = false;

        try
        {
            _capture = new MmsAudioCapture(_settings.Audio);
            _capture.FrameCaptured += OnLocalFrame;
            _capture.ErrorOccurred += msg => ErrorOccurred?.Invoke(msg);
            _capture.Start();
            captureStarted = true;
        }
        catch (Exception ex)
        {
            // Listening is more important than capture. We continue so the user
            // can still hear remote audio.
            ErrorOccurred?.Invoke("启动录音失败:" + ex.Message + " (只能收听,无法发送)");
            _capture?.Dispose();
            _capture = null;
        }

        try
        {
            _playback = new MmsAudioPlayback(_settings.Audio);
            _playback.ErrorOccurred += msg => ErrorOccurred?.Invoke(msg);
            _playback.Start();
            playbackStarted = true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke("启动播放失败:" + ex.Message + " (无法听到远端)");
            _playback?.Dispose();
            _playback = null;
        }

        IsListening = true;
        StatusChanged?.Invoke("已开始在端口 " + _settings.ListenPort + " 上监听");
        if (captureStarted && playbackStarted)
        {
            StatusChanged?.Invoke("音频设备已就绪,可以收听和按住说话");
        }
        else if (!captureStarted && playbackStarted)
        {
            StatusChanged?.Invoke("当前为降级模式:只能收听远端音频,无法发送语音");
        }
        else if (captureStarted)
        {
            StatusChanged?.Invoke("当前为降级模式:可以发送语音,但无法听到远端音频");
        }
        else
        {
            StatusChanged?.Invoke("当前为收发受限模式:录音和播放都未启动,无法发送语音也无法听到远端音频");
        }
    }

    public void StopListening()
    {
        if (!IsListening) return;
        if (IsTransmitting) StopTransmit();
        try { _capture?.Stop(); } catch { /* ignore */ }
        try { _playback?.Stop(); } catch { /* ignore */ }
        _capture?.Dispose();
        _playback?.Dispose();
        _capture = null;
        _playback = null;
        ResetVoiceEnhancer();
        _udp.StopListening();
        IsListening = false;
        StatusChanged?.Invoke("已停止监听");
    }

    public void SetTarget(string? ipAddress)
    {
        CurrentTargetIp = string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress.Trim();
        _udp.SetTarget(CurrentTargetIp);
    }

    /// <summary>Begin local PTT. Sends a press packet and starts forwarding audio.</summary>
    public void StartTransmit()
    {
        if (string.IsNullOrEmpty(CurrentTargetIp))
        {
            ErrorOccurred?.Invoke("请先设置目标 IP 地址再按住说话");
            return;
        }
        if (_capture == null)
        {
            ErrorOccurred?.Invoke("录音未启动,无法发送");
            return;
        }
        if (!_transmitState.TryStart()) return;
        try { _udp.SendPress(); } catch { /* ignore */ }
        TransmitStateChanged?.Invoke(true);
        StatusChanged?.Invoke("正在向 " + CurrentTargetIp + " 发送");
    }

    public void StopTransmit()
    {
        if (!_transmitState.TryStop()) return;
        try { _udp.SendRelease(); } catch { /* ignore */ }
        TransmitStateChanged?.Invoke(false);
        StatusChanged?.Invoke("已松开,停止发送");
    }

    public void ClearPlayback() => _playback?.ClearQueue();

    public void ResetVoiceEnhancer()
    {
        lock (_voiceEnhancerLock)
        {
            _voiceEnhancer = null;
        }
    }

    private void OnLocalFrame(byte[] pcm)
    {
        if (!IsTransmitting) return;
        if (!_settings.Audio.Enhancement.Enabled)
        {
            if (!IsTransmitting) return;
            _udp.SendAudioFrame(pcm);
            return;
        }

        try
        {
            byte[] enhanced;
            lock (_voiceEnhancerLock)
            {
                if (!IsTransmitting) return;
                if (_voiceEnhancer == null || !_voiceEnhancer.Matches(_settings.Audio))
                {
                    _voiceEnhancer = new VoiceEnhancer(_settings.Audio);
                }

                enhanced = _voiceEnhancer.ProcessPcm16Mono(pcm);
            }

            if (!IsTransmitting) return;
            _udp.SendAudioFrame(enhanced);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke("语音增强处理失败,已停止发送且未回退到未增强音频:" + ex.Message);
            StopTransmit();
        }
    }

    private void OnRemoteAudio(byte[] pcm)
    {
        _playback?.SubmitFrame(Pcm16Frame.ApplyVolume(pcm, _settings.Ui.OutputVolume));
        var now = DateTime.UtcNow;
        var previous = _lastRemoteAudioAt;
        _lastRemoteAudioAt = now;
        // Fire a "still receiving" event every ~250ms while audio is flowing.
        if ((now - previous).TotalMilliseconds > 250 || previous == DateTime.MinValue)
        {
            RemoteAudioActiveChanged?.Invoke(now);
        }
    }

    private void OnRemotePtt(bool pressing)
    {
        _remoteIsPressing = pressing;
        RemotePttStateChanged?.Invoke(pressing);
        if (pressing)
        {
            StatusChanged?.Invoke("远端正在说话");
        }
        else
        {
            StatusChanged?.Invoke("远端停止说话");
        }
    }

    public void Dispose()
    {
        StopListening();
        _udp.Dispose();
    }
}
