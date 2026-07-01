using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using LanPttIntercom.Models;

namespace LanPttIntercom.Audio;

/// <summary>
/// Real-time audio playback using the Windows Multimedia API (winmm.dll).
/// </summary>
/// <remarks>
/// <para>
/// Uses P/Invoke into the built-in <c>winmm.dll</c> (no third-party dependency).
/// </para>
/// <para>
/// The WinMM driver retains a pointer to each <c>WAVEHDR</c> we pass it
/// until the buffer is done. We therefore allocate each header with
/// <see cref="Marshal.AllocHGlobal(int)"/> and keep the unmanaged pointer
/// stable for the lifetime of the slot. The PCM data is a pinned managed
/// byte array so <c>lpData</c> points to a stable address too.
/// </para>
/// <para>
/// Concurrency model: the driver callback (<c>WOM_DONE</c>), the pump thread
/// (which calls <c>waveOutWrite</c>), and <c>Stop</c> must not contend on the
/// same lock with the device, because <c>waveOutReset</c> / <c>waveOutClose</c>
/// may synchronously invoke the callback. We avoid the deadlock by:
/// 1. The callback takes NO lock. It only updates <c>InUse</c> on the
///    matching slot (an internal flag; safe under memory model rules because
///    it is a single-byte write that the pump reads once before calling
///    <c>waveOutWrite</c>).
/// 2. <c>Stop</c> sets the <c>_stopping</c> flag, signals the pump, waits
///    for the pump to fully exit, then performs device teardown and
///    resource cleanup outside any lock.
/// </para>
/// <para>
/// <c>Start</c> is transactional: if any step throws (waveOutOpen,
/// waveOutPrepareHeader), all partial work is cleaned up before the
/// exception propagates.
/// </para>
/// </remarks>
public sealed class MmsAudioPlayback : IDisposable
{
    private readonly AudioSettings _settings;

    // --- State ---
    private int _running;
    private int _stopping;
    private int _starting; // serializes concurrent Start() calls

    private IntPtr _deviceHandle = IntPtr.Zero;
    private WaveOutProc? _callback;

    private const int HeaderCount = 8;
    private struct PlaySlot
    {
        public IntPtr HeaderPtr;     // unmanaged WAVEHDR (stable)
        public GCHandle DataPin;     // keeps the byte[] alive and pinned
        public IntPtr DataPtr;       // = DataPin.AddrOfPinnedObject()
        public int Size;
        public volatile int InUse;   // 0 = free, 1 = submitted to driver
    }
    private readonly PlaySlot[] _slots = new PlaySlot[HeaderCount];

    private Thread? _pumpThread;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _pendingSignal = new(false);

    /// <summary>PCM frames (raw bytes) to be played. Internal jitter queue.</summary>
    private readonly Queue<byte[]> _pending = new();
    private int _headerSize;

    public event Action<string>? ErrorOccurred;

    public MmsAudioPlayback(AudioSettings settings)
    {
        _settings = settings;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public static IReadOnlyList<MmsDeviceInfo> EnumerateOutputDevices()
    {
        var list = new List<MmsDeviceInfo>();
        int count = waveOutGetNumDevs();
        for (int i = 0; i < count; i++)
        {
            var caps = new WAVEOUTCAPS();
            int size = Marshal.SizeOf<WAVEOUTCAPS>();
            int hr = waveOutGetDevCaps((UIntPtr)i, out caps, size);
            if (hr == 0)
            {
                list.Add(new MmsDeviceInfo(i, caps.szPname));
            }
        }
        return list;
    }

    /// <summary>
    /// Opens the device and starts the pump. Transactional: on any failure,
    /// all partial work is cleaned up before the exception is rethrown.
    /// </summary>
    public void Start()
    {
        // Serialize concurrent Start() calls: only one thread may pass.
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return;
        try
        {
            if (Volatile.Read(ref _running) != 0 || Volatile.Read(ref _stopping) != 0) return;

            var cts = new CancellationTokenSource();
            var thread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "MmsAudioPlayback.Pump"
            };
            _cts = cts;
            _callback = OnWaveOut;

            IntPtr handle = IntPtr.Zero;
            bool deviceOpened = false;
            int headerSize = Marshal.SizeOf<WAVEHDR>();
            int allocatedCount = 0;
            IntPtr[] allocatedHeaderPtrs = new IntPtr[HeaderCount];
            GCHandle[] allocatedPins = new GCHandle[HeaderCount];

            try
            {
                var format = new WAVEFORMATEX
                {
                    wFormatTag = WAVE_FORMAT_PCM,
                    nChannels = (ushort)_settings.Channels,
                    nSamplesPerSec = (uint)_settings.SampleRate,
                    nAvgBytesPerSec = (uint)(_settings.SampleRate * _settings.Channels * _settings.BitsPerSample / 8),
                    nBlockAlign = (ushort)(_settings.Channels * _settings.BitsPerSample / 8),
                    wBitsPerSample = (ushort)_settings.BitsPerSample,
                    cbSize = 0
                };

                int deviceId = _settings.OutputDeviceId < 0 ? -1 : _settings.OutputDeviceId;
                int hr = waveOutOpen(
                    out handle,
                    deviceId,
                    ref format,
                    _callback,
                    IntPtr.Zero,
                    CALLBACK_FUNCTION);
                if (hr != 0)
                {
                    throw new InvalidOperationException($"waveOutOpen 失败,错误码 {hr} (可能是没有可用的播放设备或格式不支持)");
                }
                deviceOpened = true;

                for (int i = 0; i < HeaderCount; i++)
                {
                    var slot = AllocateSlot(_settings.FrameBytes, headerSize);
                    // Track the slot in the cleanup arrays immediately so the
                    // catch path frees HeaderPtr/DataPin even if waveOutPrepareHeader
                    // fails (or any later step fails) before this slot is recorded.
                    _slots[i] = slot;
                    allocatedHeaderPtrs[i] = slot.HeaderPtr;
                    allocatedPins[i] = slot.DataPin;
                    allocatedCount = i + 1;
                    hr = waveOutPrepareHeader(handle, slot.HeaderPtr, headerSize);
                    if (hr != 0) throw new InvalidOperationException($"waveOutPrepareHeader 失败,错误码 {hr}");
                }

                _headerSize = headerSize;
                _deviceHandle = handle;
                Volatile.Write(ref _running, 1);
            }
            catch
            {
                // Transactional cleanup. Walk back what was allocated.
                if (deviceOpened && handle != IntPtr.Zero)
                {
                    try { waveOutReset(handle); } catch { /* ignore */ }
                    for (int i = 0; i < allocatedCount; i++)
                    {
                        if (allocatedHeaderPtrs[i] != IntPtr.Zero)
                        {
                            try { waveOutUnprepareHeader(handle, allocatedHeaderPtrs[i], headerSize); }
                            catch { /* ignore */ }
                        }
                    }
                }
                for (int i = 0; i < allocatedCount; i++)
                {
                    if (allocatedHeaderPtrs[i] != IntPtr.Zero)
                    {
                        try { Marshal.FreeHGlobal(allocatedHeaderPtrs[i]); } catch { /* ignore */ }
                    }
                    if (allocatedPins[i].IsAllocated)
                    {
                        try { allocatedPins[i].Free(); } catch { /* ignore */ }
                    }
                }
                if (deviceOpened && handle != IntPtr.Zero)
                {
                    try { waveOutClose(handle); } catch { /* ignore */ }
                }
                for (int i = 0; i < HeaderCount; i++) _slots[i] = default;
                _callback = null;
                try { cts.Cancel(); } catch { /* ignore */ }
                Volatile.Write(ref _stopping, 0);
                throw;
            }

            thread.Start();
            _pumpThread = thread;
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    /// <summary>
    /// Stops playback and releases all resources. Safe to call multiple times
    /// and safe to call when Start partially succeeded.
    /// </summary>
    public void Stop()
    {
        Volatile.Write(ref _stopping, 1);
        Volatile.Write(ref _running, 0);

        // Wake and stop the pump thread. The pump checks _stopping and exits;
        // we wait for the join so no waveOutWrite call is in progress.
        try { _pendingSignal.Set(); } catch { /* ignore */ }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _pumpThread?.Join(1000); } catch { /* ignore */ }
        // After Join, no more waveOutWrite happens. The WOM_DONE callback
        // may still fire from waveOutReset below; it only touches InUse and
        // does not call any winmm function, so it is safe even after we
        // close the device (we set _stopping=1 which makes it a no-op via
        // the callback handler).
        _pumpThread = null;

        var handle = _deviceHandle;
        var cts = _cts;
        _deviceHandle = IntPtr.Zero;
        _cts = null;
        _callback = null;

        int headerSize = _headerSize;

        if (handle != IntPtr.Zero)
        {
            try { waveOutReset(handle); } catch { /* ignore */ }
            for (int i = 0; i < HeaderCount; i++)
            {
                var s = _slots[i];
                if (s.HeaderPtr != IntPtr.Zero)
                {
                    try { waveOutUnprepareHeader(handle, s.HeaderPtr, headerSize); } catch { /* ignore */ }
                }
            }
        }
        for (int i = 0; i < HeaderCount; i++)
        {
            var s = _slots[i];
            if (s.HeaderPtr != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(s.HeaderPtr); } catch { /* ignore */ }
            }
            if (s.DataPin.IsAllocated)
            {
                try { s.DataPin.Free(); } catch { /* ignore */ }
            }
            _slots[i] = default;
        }
        if (handle != IntPtr.Zero)
        {
            try { waveOutClose(handle); } catch { /* ignore */ }
        }

        lock (_pending) { _pending.Clear(); }
        try { cts?.Cancel(); } catch { /* ignore */ }

        Volatile.Write(ref _stopping, 0);
    }

    /// <summary>Enqueue a PCM frame for playback. The frame is copied into the next available slot.</summary>
    public void SubmitFrame(byte[] pcm)
    {
        if (Volatile.Read(ref _running) == 0 || Volatile.Read(ref _stopping) != 0) return;
        if (pcm == null || pcm.Length == 0) return;
        lock (_pending)
        {
            // If the queue is too long (network flooding), drop the oldest to bound latency.
            while (_pending.Count > 64) _pending.Dequeue();
            _pending.Enqueue(pcm);
        }
        _pendingSignal.Set();
    }

    public void ClearQueue()
    {
        lock (_pending) { _pending.Clear(); }
    }

    private static PlaySlot AllocateSlot(int bytes, int headerSize)
    {
        var data = new byte[bytes];
        var dataPin = GCHandle.Alloc(data, GCHandleType.Pinned);
        IntPtr dataPtr = dataPin.AddrOfPinnedObject();

        IntPtr headerPtr = Marshal.AllocHGlobal(headerSize);
        for (int i = 0; i < headerSize; i++) Marshal.WriteByte(headerPtr, i, 0);
        var hdr = new WAVEHDR
        {
            lpData = dataPtr,
            dwBufferLength = (uint)bytes,
            dwBytesRecorded = 0,
            dwUser = IntPtr.Zero,
            dwFlags = 0,
            dwLoops = 0,
            lpNext = IntPtr.Zero,
            reserved = IntPtr.Zero
        };
        Marshal.StructureToPtr(hdr, headerPtr, false);

        return new PlaySlot
        {
            HeaderPtr = headerPtr,
            DataPin = dataPin,
            DataPtr = dataPtr,
            Size = bytes,
            InUse = 0
        };
    }

    private void PumpLoop()
    {
        var cts = _cts;
        var ct = cts?.Token ?? CancellationToken.None;
        try
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _stopping) == 0 && Volatile.Read(ref _running) != 0)
            {
                if (!TrySubmitNext(out string? err))
                {
                    if (err != null) ErrorOccurred?.Invoke(err);
                    _pendingSignal.Wait(20, ct);
                    _pendingSignal.Reset();
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { ErrorOccurred?.Invoke("音频播放泵线程异常:" + ex.Message); }
    }

    /// <summary>
    /// If a slot is free and a frame is queued, copy the frame into the slot's
    /// PCM buffer, update dwFlags on the WAVEHDR, and submit it. Uses the same
    /// unmanaged header pointer across the lifetime of the slot.
    /// </summary>
#pragma warning disable CS0420  // Volatile semantics apply to the InUse field access via Volatile.Read/Write.
    private bool TrySubmitNext(out string? err)
    {
        err = null;
        int slot = -1;
        for (int i = 0; i < HeaderCount; i++)
        {
            if (_slots[i].HeaderPtr != IntPtr.Zero && _slots[i].InUse == 0)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) return false;

        byte[]? frame;
        lock (_pending)
        {
            if (_pending.Count == 0) return false;
            frame = _pending.Dequeue();
        }
        if (frame == null) return false;

        try
        {
            var s = _slots[slot];
            int copyLen = Math.Min(frame.Length, s.Size);
            if (s.DataPtr != IntPtr.Zero && copyLen > 0)
            {
                Marshal.Copy(frame, 0, s.DataPtr, copyLen);
            }

            // Update the unmanaged WAVEHDR in place. Keep WHDR_PREPARED
            // (0x02), and clear only WHDR_DONE (0x01) / WHDR_INQUEUE (0x10)
            // so the driver treats this as a fresh prepared buffer.
            var existing = Marshal.PtrToStructure<WAVEHDR>(s.HeaderPtr);
            var hdr = new WAVEHDR
            {
                lpData = s.DataPtr,
                dwBufferLength = (uint)copyLen,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = PrepareHeaderFlagsForSubmit(existing.dwFlags),
                dwLoops = 0,
                lpNext = IntPtr.Zero,
                reserved = IntPtr.Zero
            };
            Marshal.StructureToPtr(hdr, s.HeaderPtr, false);

            // Mark InUse BEFORE calling waveOutWrite so the callback can clear
            // it when WOM_DONE fires. Volatile write ensures the pump's write
            // is visible to the driver callback thread.
            Volatile.Write(ref _slots[slot].InUse, 1);
            int hr = waveOutWrite(_deviceHandle, s.HeaderPtr, _headerSize);
            if (hr != 0)
            {
                Volatile.Write(ref _slots[slot].InUse, 0);
                err = "waveOutWrite 失败,错误码 " + hr;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _slots[slot].InUse, 0);
            err = "提交播放帧异常:" + ex.Message;
            return false;
        }
    }

    private void OnWaveOut(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        const uint WOM_DONE = 0x3BD;
        if (uMsg != WOM_DONE) return;
        if (dwParam1 == IntPtr.Zero) return;
        // Take no lock and call no winmm function here. The InUse write is
        // a single volatile int, and the pump thread reads it before each
        // waveOutWrite. The only other write is _pendingSignal.Set, which
        // is itself thread-safe.
        if (Volatile.Read(ref _stopping) != 0) return;
        try
        {
            // dwParam1 is the same unmanaged WAVEHDR* we passed to
            // waveOutPrepareHeader and waveOutWrite. Identify the slot by
            // matching the lpData field.
            var hdr = Marshal.PtrToStructure<WAVEHDR>(dwParam1);
            IntPtr lpData = hdr.lpData;
            for (int i = 0; i < HeaderCount; i++)
            {
                if (_slots[i].DataPtr == lpData)
                {
                    Volatile.Write(ref _slots[i].InUse, 0);
                    break;
                }
            }
            _pendingSignal.Set();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke("音频播放回调异常:" + ex.Message);
        }
    }
#pragma warning restore CS0420

    public void Dispose() => Stop();

    #region P/Invoke

    private const int WAVE_FORMAT_PCM = 0x0001;
    private const int CALLBACK_FUNCTION = 0x00030000;
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_INQUEUE = 0x00000010;

    internal static uint PrepareHeaderFlagsForSubmit(uint currentFlags)
    {
        return currentFlags & ~(WHDR_DONE | WHDR_INQUEUE);
    }

    private delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public ushort wReserved2;
        public uint dwSupport;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int waveOutGetDevCaps(UIntPtr uDeviceID, out WAVEOUTCAPS pwoc, int cbwoc);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WAVEFORMATEX pwfx, WaveOutProc? dwCallback, IntPtr dwInstance, int fdwOpen);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutClose(IntPtr hwo);

    #endregion
}
