using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanPttIntercom.Models;

namespace LanPttIntercom.Network;

/// <summary>
/// UDP voice transport for the LAN intercom.
/// </summary>
/// <remarks>
/// <para>One <see cref="UdpClient"/> is bound to the listen port for both
/// receiving incoming audio and sending outgoing audio. Each datagram has a
/// small header followed by the raw PCM frame:</para>
/// <code>
/// [byte 0]   = packet type: 1 = audio, 2 = press, 3 = release
/// [byte 1]   = reserved
/// [byte 2..3]= sequence number (big-endian, audio only)
/// [byte 4..7]= timestamp ms (big-endian, audio only)
/// [byte 8..N]= PCM payload (audio only)
/// </code>
/// <para>The receiver filters out its own packets (matching the bound local
/// endpoint) and only feeds remote frames to the playback queue.</para>
/// </remarks>
public sealed class VoiceUdpClient : IDisposable
{
    public const byte PacketAudio = 1;
    public const byte PacketPress = 2;
    public const byte PacketRelease = 3;

    private const int HeaderSize = 8;

    private readonly int _port;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    private ushort _outSequence;
    private IPEndPoint? _currentTarget;
    private readonly object _targetLock = new();

    public event Action<byte[]>? AudioFrameReceived;
    public event Action<bool>? RemotePttStateChanged; // true = press, false = release
    public event Action<string>? ErrorOccurred;
    public event Action? ListeningStateChanged;

    public int Port => _port;
    public bool IsListening => _udp != null;

    public VoiceUdpClient(int port)
    {
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
    }

    /// <summary>Open the UDP socket and start the receive loop. Idempotent.</summary>
    public void StartListening()
    {
        if (_udp != null) return;
        try
        {
            _udp = new UdpClient(_port) { EnableBroadcast = false };
            _cts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoop(_udp, _cts.Token));
            ListeningStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _udp?.Close();
            _udp = null;
            ErrorOccurred?.Invoke("启动监听失败:" + ex.Message);
            throw;
        }
    }

    public void StopListening()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp = null;
        _cts = null;
        _receiveTask = null;
        ListeningStateChanged?.Invoke();
    }

    /// <summary>Set the target peer. Pass null to clear (idle, no transmit).</summary>
    public void SetTarget(string? ipAddress)
    {
        lock (_targetLock)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                _currentTarget = null;
                return;
            }
            if (!IPAddress.TryParse(ipAddress, out var addr))
            {
                _currentTarget = null;
                return;
            }
            _currentTarget = new IPEndPoint(addr, _port);
        }
    }

    public string? CurrentTargetIp
    {
        get
        {
            lock (_targetLock)
            {
                return _currentTarget?.Address.ToString();
            }
        }
    }

    public void SendPress()
    {
        SendControl(PacketPress);
    }

    public void SendRelease()
    {
        SendControl(PacketRelease);
    }

    private void SendControl(byte type)
    {
        IPEndPoint? target;
        lock (_targetLock) { target = _currentTarget; }
        if (target == null) return;
        var client = _udp;
        if (client == null) return;

        var packet = new byte[HeaderSize];
        packet[0] = type;
        try
        {
            client.Send(packet, packet.Length, target);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke("发送控制包失败:" + ex.Message);
        }
    }

    public void SendAudioFrame(byte[] pcm)
    {
        if (pcm == null || pcm.Length == 0) return;
        IPEndPoint? target;
        lock (_targetLock) { target = _currentTarget; }
        if (target == null) return;
        var client = _udp;
        if (client == null) return;

        var packet = new byte[HeaderSize + pcm.Length];
        packet[0] = PacketAudio;
        packet[1] = 0;
        unchecked
        {
            _outSequence++;
        }
        packet[2] = (byte)(_outSequence >> 8);
        packet[3] = (byte)(_outSequence & 0xFF);
        uint tsMs = (uint)Environment.TickCount;
        packet[4] = (byte)(tsMs >> 24);
        packet[5] = (byte)(tsMs >> 16);
        packet[6] = (byte)(tsMs >> 8);
        packet[7] = (byte)(tsMs & 0xFF);
        Buffer.BlockCopy(pcm, 0, packet, HeaderSize, pcm.Length);
        try
        {
            client.Send(packet, packet.Length, target);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke("发送音频包失败:" + ex.Message);
        }
    }

    private void ReceiveLoop(UdpClient client, CancellationToken ct)
    {
        var anyRemote = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            IPEndPoint? remote = null;
            byte[] data;
            try
            {
                data = client.Receive(ref anyRemote);
                remote = anyRemote;
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("接收失败:" + ex.Message);
                continue;
            }
            if (data == null || data.Length < HeaderSize) continue;
            if (remote == null) continue;

            // Filter out loopback (our own sent packets).
            if (IPAddress.IsLoopback(remote.Address))
            {
                // Accept loopback only if the source port differs from our bound port;
                // this still counts as "ours" in practice.
                continue;
            }
            if (IsLocalAddress(remote.Address))
            {
                continue;
            }

            byte type = data[0];
            if (type == PacketAudio)
            {
                var pcm = new byte[data.Length - HeaderSize];
                Buffer.BlockCopy(data, HeaderSize, pcm, 0, pcm.Length);
                try { AudioFrameReceived?.Invoke(pcm); }
                catch (Exception ex) { ErrorOccurred?.Invoke("处理远端音频异常:" + ex.Message); }
            }
            else if (type == PacketPress)
            {
                RemotePttStateChanged?.Invoke(true);
            }
            else if (type == PacketRelease)
            {
                RemotePttStateChanged?.Invoke(false);
            }
            // Unknown types: drop silently.
        }
    }

    private static bool IsLocalAddress(IPAddress remote)
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var addr in host.AddressList)
            {
                if (addr.Equals(remote)) return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    public void Dispose()
    {
        StopListening();
    }
}
