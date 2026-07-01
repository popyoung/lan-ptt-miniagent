using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using LanPttIntercom.Models;

namespace LanPttIntercom.Audio;

/// <summary>
/// Real-time audio capture using the Windows Multimedia API (winmm.dll).
/// </summary>
/// <remarks>
/// <para>
/// Uses P/Invoke into the built-in <c>winmm.dll</c> (no third-party dependency).
/// </para>
/// <para>
/// WinMM holds onto each <c>WAVEHDR</c> pointer passed to <c>waveInPrepareHeader</c>
/// / <c>waveInAddBuffer</c> for the lifetime of the buffer. We therefore allocate
/// each <c>WAVEHDR</c> with <see cref="Marshal.AllocHGlobal(int)"/> and keep the
/// unmanaged pointer stable for as long as the buffer is in use. The PCM data
/// itself is a pinned managed byte array (kept alive by a GCHandle) so
/// <c>lpData</c> points to a stable address too.
/// </para>
/// <para>
/// To re-queue a buffer after the driver filled it, the callback's
/// <c>dwParam1</c> is the stable unmanaged header address; we re-queue by
/// writing the updated fields back to that same memory and calling
/// <c>waveInAddBuffer</c> with the same pointer (no struct copies).
/// </para>
/// <para>
/// Concurrency model: the driver callback and the <c>Stop</c> method must not
/// take the same lock, because <c>waveInStop</c> / <c>waveInReset</c> may
/// synchronously invoke the callback for any in-flight buffers. We avoid the
/// resulting deadlock by:
///
/// 1. The callback takes NO lock. It only reads atomic state flags
///    (<see cref="_running"/>, <see cref="_stopping"/>) and an in-flight
///    counter (<see cref="_callbacksInFlight"/>).
/// 2. <c>Stop</c> sets the <see cref="_stopping"/> flag, waits for
///    in-flight callbacks to drain (spin on the counter), then performs
///    device teardown and resource cleanup outside any lock.
/// </para>
/// <para>
/// <c>Start</c> is transactional: if any step throws (waveInOpen,
/// waveInPrepareHeader, waveInAddBuffer, waveInStart), all partial work is
/// cleaned up before the exception propagates. Cleanup is the same code path
/// used by <c>Stop</c>, so it does not depend on <see cref="_running"/>.
/// </para>
/// </remarks>
public sealed class MmsAudioCapture : IDisposable
{
    private readonly AudioSettings _settings;
    private readonly int _frameBytes;

    // --- State (all accessed from multiple threads; use Volatile) ---
    private int _running;          // 0/1 atomic flag: set by Start after commit, cleared by Stop
    private int _stopping;         // 0/1 atomic flag: set by Stop, set transiently during Start cleanup
    private int _starting;         // 0/1 atomic flag: set during Start() to serialize concurrent Start calls
    private int _callbacksInFlight; // counter; callback increments on entry, decrements on exit

    private IntPtr _deviceHandle = IntPtr.Zero;

    private struct CaptureSlot
    {
        public IntPtr HeaderPtr;     // unmanaged WAVEHDR
        public GCHandle DataPin;     // keeps the byte[] alive and pinned
        public IntPtr DataPtr;       // = DataPin.AddrOfPinnedObject()
        public int Size;             // byte count
    }
    private readonly List<CaptureSlot> _slots = new();
    private const int BufferCount = 4;

    private WaveInProc? _callback;
    private Thread? _dispatchThread;
    private CancellationTokenSource? _cts;
    private BlockingCollection<byte[]>? _readyQueue;

    public event Action<byte[]>? FrameCaptured;
    public event Action<string>? ErrorOccurred;

    public MmsAudioCapture(AudioSettings settings)
    {
        _settings = settings;
        _frameBytes = settings.FrameBytes;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public static IReadOnlyList<MmsDeviceInfo> EnumerateInputDevices()
    {
        var list = new List<MmsDeviceInfo>();
        int count = waveInGetNumDevs();
        for (int i = 0; i < count; i++)
        {
            var caps = new WAVEINCAPS();
            int size = Marshal.SizeOf<WAVEINCAPS>();
            int hr = waveInGetDevCaps((UIntPtr)i, out caps, size);
            if (hr == 0)
            {
                list.Add(new MmsDeviceInfo(i, caps.szPname));
            }
        }
        return list;
    }

    /// <summary>
    /// Opens the device and starts capture. Transactional: on any failure,
    /// all partial work is cleaned up before the exception is rethrown.
    /// </summary>
    public void Start()
    {
        // Serialize concurrent Start() calls: only one thread may pass.
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return;
        try
        {
            if (Volatile.Read(ref _running) != 0 || Volatile.Read(ref _stopping) != 0) return;

            // --- Phase 1: pre-create managed resources.
            var queue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 64);
            var cts = new CancellationTokenSource();
            var thread = new Thread(DispatchLoop)
            {
                IsBackground = true,
                Name = "MmsAudioCapture.Dispatch"
            };
            _readyQueue = queue;
            _cts = cts;
            _callback = OnWaveIn;
            thread.Start();

            // --- Phase 2: open device + allocate/prepare/start slots.
            // Use local "allocated" lists so we can clean up on failure WITHOUT
            // mutating the instance fields.
            var allocated = new List<CaptureSlot>(BufferCount);
            bool deviceOpened = false;
            IntPtr deviceHandle = IntPtr.Zero;
            int headerSize = Marshal.SizeOf<WAVEHDR>();

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

                int deviceId = _settings.InputDeviceId < 0 ? -1 : _settings.InputDeviceId;
                int hr = waveInOpen(
                    out deviceHandle,
                    deviceId,
                    ref format,
                    _callback,
                    IntPtr.Zero,
                    CALLBACK_FUNCTION);
                if (hr != 0)
                {
                    throw new InvalidOperationException($"waveInOpen 失败,错误码 {hr} (可能是没有可用的录音设备或格式不支持)");
                }
                deviceOpened = true;

                for (int i = 0; i < BufferCount; i++)
                {
                    var slot = AllocateSlot(_frameBytes, headerSize);
                    // Track the slot immediately so the cleanup path frees
                    // HeaderPtr/DataPin even if the next winmm call fails
                    // before the slot is fully prepared+queued.
                    allocated.Add(slot);
                    hr = waveInPrepareHeader(deviceHandle, slot.HeaderPtr, headerSize);
                    if (hr != 0) throw new InvalidOperationException($"waveInPrepareHeader 失败,错误码 {hr}");
                    hr = waveInAddBuffer(deviceHandle, slot.HeaderPtr, headerSize);
                    if (hr != 0) throw new InvalidOperationException($"waveInAddBuffer 失败,错误码 {hr}");
                }

                hr = waveInStart(deviceHandle);
                if (hr != 0) throw new InvalidOperationException($"waveInStart 失败,错误码 {hr}");

                // --- Phase 3: commit. Past this point, Stop() must clean up.
                _deviceHandle = deviceHandle;
                foreach (var s in allocated) _slots.Add(s);
                Volatile.Write(ref _running, 1);
            }
            catch
            {
                // Transactional cleanup: do NOT touch instance fields that weren't
                // already committed. Use the locals.
                TeardownPartial(deviceHandle, deviceOpened, allocated, headerSize);
                try { queue.CompleteAdding(); } catch { /* ignore */ }
                try { cts.Cancel(); } catch { /* ignore */ }
                try { thread.Join(500); } catch { /* ignore */ }
                _readyQueue = null;
                _cts = null;
                _dispatchThread = null;
                _callback = null;
                Volatile.Write(ref _stopping, 0);
                throw;
            }

            _dispatchThread = thread;
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    /// <summary>
    /// Stops capture and releases all resources. Safe to call multiple times
    /// and safe to call when Start partially succeeded.
    /// </summary>
    public void Stop()
    {
        // Set stopping and clear running under no lock; both are atomic.
        Volatile.Write(ref _stopping, 1);
        Volatile.Write(ref _running, 0);

        // Wait for any callback currently inside OnWaveIn to finish. The
        // callback increments _callbacksInFlight on entry and decrements on
        // exit; between the two it may call waveInAddBuffer using the device
        // handle we are about to close. We must wait before teardown.
        WaitForCallbacksToDrain();

        // Capture instance state into locals, then null out instance fields
        // so any reentrant call into us is a no-op.
        var handle = _deviceHandle;
        var slotsCopy = new List<CaptureSlot>(_slots);
        var queue = _readyQueue;
        var cts = _cts;
        var thread = _dispatchThread;

        _deviceHandle = IntPtr.Zero;
        _slots.Clear();
        _readyQueue = null;
        _cts = null;
        _dispatchThread = null;
        _callback = null;

        int headerSize = Marshal.SizeOf<WAVEHDR>();

        if (handle != IntPtr.Zero)
        {
            try { waveInStop(handle); } catch { /* ignore */ }
            try { waveInReset(handle); } catch { /* ignore */ }
            foreach (var s in slotsCopy)
            {
                if (s.HeaderPtr != IntPtr.Zero)
                {
                    try { waveInUnprepareHeader(handle, s.HeaderPtr, headerSize); } catch { /* ignore */ }
                }
            }
        }
        foreach (var s in slotsCopy)
        {
            if (s.HeaderPtr != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(s.HeaderPtr); } catch { /* ignore */ }
            }
            if (s.DataPin.IsAllocated)
            {
                try { s.DataPin.Free(); } catch { /* ignore */ }
            }
        }
        if (handle != IntPtr.Zero)
        {
            try { waveInClose(handle); } catch { /* ignore */ }
        }

        // Tear down the dispatch thread.
        try { queue?.CompleteAdding(); } catch { /* ignore */ }
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { thread?.Join(500); } catch { /* ignore */ }

        Volatile.Write(ref _stopping, 0);
    }

    private void TeardownPartial(IntPtr handle, bool handleOpen, List<CaptureSlot> slots, int headerSize)
    {
        if (handleOpen && handle != IntPtr.Zero)
        {
            try { waveInStop(handle); } catch { /* ignore */ }
            try { waveInReset(handle); } catch { /* ignore */ }
            foreach (var s in slots)
            {
                if (s.HeaderPtr != IntPtr.Zero)
                {
                    try { waveInUnprepareHeader(handle, s.HeaderPtr, headerSize); } catch { /* ignore */ }
                }
            }
        }
        foreach (var s in slots)
        {
            if (s.HeaderPtr != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(s.HeaderPtr); } catch { /* ignore */ }
            }
            if (s.DataPin.IsAllocated)
            {
                try { s.DataPin.Free(); } catch { /* ignore */ }
            }
        }
        if (handleOpen && handle != IntPtr.Zero)
        {
            try { waveInClose(handle); } catch { /* ignore */ }
        }
    }

    private void WaitForCallbacksToDrain()
    {
        // The callback is fast; a short spin is fine. We bound it so a stuck
        // driver can't hang us forever.
        for (int i = 0; i < 2000; i++)
        {
            if (Volatile.Read(ref _callbacksInFlight) == 0) return;
            Thread.Sleep(1);
        }
    }

    private static CaptureSlot AllocateSlot(int bytes, int headerSize)
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

        return new CaptureSlot
        {
            HeaderPtr = headerPtr,
            DataPin = dataPin,
            DataPtr = dataPtr,
            Size = bytes
        };
    }

    private void DispatchLoop()
    {
        var queue = _readyQueue;
        var cts = _cts;
        if (queue == null) return;
        try
        {
            foreach (var frame in queue.GetConsumingEnumerable(cts?.Token ?? CancellationToken.None))
            {
                try { FrameCaptured?.Invoke(frame); }
                catch (Exception ex) { ErrorOccurred?.Invoke("处理音频帧出错:" + ex.Message); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { ErrorOccurred?.Invoke("音频分发线程异常:" + ex.Message); }
    }

    private void OnWaveIn(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        const uint WIM_DATA = 0x3C0;
        if (uMsg != WIM_DATA) return;
        if (dwParam1 == IntPtr.Zero) return;

        // Increment in-flight BEFORE checking state, so Stop's drain-wait
        // never races past us while we are still about to call waveInAddBuffer.
        Interlocked.Increment(ref _callbacksInFlight);
        try
        {
            if (Volatile.Read(ref _running) == 0 || Volatile.Read(ref _stopping) != 0) return;

            // dwParam1 is the same unmanaged WAVEHDR* the driver was given.
            var header = Marshal.PtrToStructure<WAVEHDR>(dwParam1);
            if (header.dwBytesRecorded == 0) return;

            int len = (int)header.dwBytesRecorded;
            if (len > header.dwBufferLength) len = (int)header.dwBufferLength;

            byte[] frame = new byte[len];
            if (header.lpData != IntPtr.Zero)
            {
                Marshal.Copy(header.lpData, frame, 0, len);
            }

            // Reset dwBytesRecorded and clear transient WinMM flags for the next round-trip.
            // WHDR_PREPARED must remain set after waveInPrepareHeader.
            header.dwBytesRecorded = 0;
            header.dwFlags = PrepareHeaderFlagsForReuse(header.dwFlags);
            Marshal.StructureToPtr(header, dwParam1, false);

            // Enqueue for the consumer thread. Non-blocking: if the queue is
            // full, drop this frame (real-time audio: better to drop than to
            // block the driver callback).
            var q = _readyQueue;
            if (q != null && !q.IsAddingCompleted)
            {
                if (!q.TryAdd(frame, 0))
                {
                    RateLimitWarn("音频队列已满,丢帧");
                }
            }

            // Re-queue the SAME unmanaged header pointer. The Stop drain-wait
            // ensures the device handle is still valid here.
            if (Volatile.Read(ref _stopping) == 0)
            {
                try
                {
                    int hr = waveInAddBuffer(_deviceHandle, dwParam1, Marshal.SizeOf<WAVEHDR>());
                    if (hr != 0)
                    {
                        RateLimitWarn("waveInAddBuffer 重投失败,错误码 " + hr);
                    }
                }
                catch (Exception ex)
                {
                    RateLimitWarn("重投缓冲区异常:" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            RateLimitWarn("音频回调异常:" + ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _callbacksInFlight);
        }
    }

    // Rate-limit ErrorOccurred so a stuck driver can't flood the log.
    private long _lastWarnTicks;
    private void RateLimitWarn(string msg)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastWarnTicks);
        if (now - last < 500) return;
        Interlocked.Exchange(ref _lastWarnTicks, now);
        ErrorOccurred?.Invoke(msg);
    }

    public void Dispose() => Stop();

    #region P/Invoke

    private const int WAVE_FORMAT_PCM = 0x0001;
    private const int CALLBACK_FUNCTION = 0x00030000;
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;
    private const uint WHDR_BEGINLOOP = 0x00000004;
    private const uint WHDR_INQUEUE = 0x00000010;

    internal static uint PrepareHeaderFlagsForReuse(uint currentFlags)
    {
        // Preserve WHDR_PREPARED and unrelated flags such as WHDR_BEGINLOOP;
        // clear only completion/queue state before waveInAddBuffer reuses it.
        return currentFlags & ~(WHDR_DONE | WHDR_INQUEUE);
    }

    private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

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
    private struct WAVEINCAPS
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
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInGetNumDevs();

    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int waveInGetDevCaps(UIntPtr uDeviceID, out WAVEINCAPS pwic, int cbwic);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX pwfx, WaveInProc? dwCallback, IntPtr dwInstance, int fdwOpen);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInStart(IntPtr hwi);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInStop(IntPtr hwi);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInReset(IntPtr hwi);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveInClose(IntPtr hwi);

    #endregion
}

public readonly record struct MmsDeviceInfo(int DeviceId, string Name)
{
    public override string ToString() => DeviceId < 0 ? "系统默认" : $"{DeviceId}: {Name}";
}
