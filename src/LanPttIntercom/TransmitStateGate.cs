using System.Threading;

namespace LanPttIntercom;

public sealed class TransmitStateGate
{
    private int _isTransmitting;

    public bool IsTransmitting => Volatile.Read(ref _isTransmitting) != 0;

    public bool TryStart()
    {
        return Interlocked.CompareExchange(ref _isTransmitting, 1, 0) == 0;
    }

    public bool TryStop()
    {
        return Interlocked.CompareExchange(ref _isTransmitting, 0, 1) == 1;
    }

    public void Reset()
    {
        Volatile.Write(ref _isTransmitting, 0);
    }
}
