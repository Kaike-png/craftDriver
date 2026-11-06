using System.Diagnostics;
using System.Net;

namespace CraftDriver.Core.Graph;

/// <summary>
/// Orçamento agregado por drive/direção, sem crédito acumulado nem rajada inicial. Agenda absoluta: cada pedaço
/// avança o horário previsto (início + bytes/taxa) e só se dorme quando se está adiantado em pelo menos
/// <see cref="MinSleep"/> — dormir a cada fatia de 16 KiB arredondava para ≥ 1 ms por fatia (timer do Linux) e
/// saturava em ~13 MiB/s qualquer que fosse o limite.
/// </summary>
internal sealed class TransferBandwidth(long bytesPerSecond)
{
    /// <summary>Menor espera que vale a pena pedir ao timer; adiantamentos menores ficam para o próximo pedaço.</summary>
    public static readonly TimeSpan MinSleep = TimeSpan.FromMilliseconds(5);

    /// <summary>Atraso em relação à agenda que ainda é recuperado; acima disso a transferência é tratada como ociosa.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromMilliseconds(100);

    readonly Lock _lock = new();
    long _rate = bytesPerSecond;
    /// <summary>Horário (Stopwatch) em que o último byte agendado "pode" ter sido entregue.</summary>
    long _due;

    /// <summary>Limite atual (0 = ilimitado). Pode mudar com transferências em andamento: vale do próximo pedaço em diante.</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _rate);
        set => Interlocked.Exchange(ref _rate, value);
    }

    public bool Enabled => BytesPerSecond > 0;
    public double SecondsFor(long bytes) => BytesPerSecond is var r && r > 0 ? bytes / (double)r : 0;

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        if (!Enabled || bytes == 0) return;
        ct.ThrowIfCancellationRequested();
        long target;
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            // Ocioso: recomeça a agenda agora — sem crédito acumulado para uma rajada depois. Atraso curto (o tempo
            // entre um pedaço e o próximo: resposta do servidor, leitura do disco) é recuperado, senão a sobrecarga
            // de cada pedido saía do limite.
            if (_due < now - (long)(CatchUpWindow.TotalSeconds * Stopwatch.Frequency)) _due = now;
            _due += (long)(SecondsFor(bytes) * Stopwatch.Frequency);
            target = _due;
        }
        try
        {
            // paga antes de entregar (sem rajada inicial); tempo monotônico, e o atraso do timer não se acumula
            // porque o alvo é absoluto
            while (Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), target) is var ahead && ahead >= MinSleep)
                await Task.Delay(ahead < TimeSpan.FromSeconds(60) ? ahead : TimeSpan.FromSeconds(60), ct);
        }
        catch (OperationCanceledException)
        {
            // cancelado: devolve à agenda o que não foi entregue, para não atrasar a próxima transferência
            lock (_lock)
            {
                var now = Stopwatch.GetTimestamp();
                var unused = Math.Min(target - now, (long)(SecondsFor(bytes) * Stopwatch.Frequency));
                if (unused > 0) _due -= unused;
            }
            throw;
        }
    }
}

internal sealed class BandwidthContent(byte[] bytes, int count, TransferBandwidth bandwidth) : HttpContent
{
    public double PacingSeconds => bandwidth.SecondsFor(count);
    protected override bool TryComputeLength(out long length) { length = count; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        using var destination = new BandwidthWriteStream(stream, bandwidth);
        await destination.WriteAsync(bytes.AsMemory(0, count), ct);
    }
}

/// <summary>Limita os bytes entregues ao destino; não fecha o stream do chamador.</summary>
internal sealed class BandwidthWriteStream(Stream inner, TransferBandwidth bandwidth) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use WriteAsync");
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        while (!buffer.IsEmpty)
        {
            var count = Math.Min(buffer.Length, 16 * 1024);
            await bandwidth.WaitAsync(count, ct);
            await inner.WriteAsync(buffer[..count], ct);
            buffer = buffer[count..];
        }
    }
}
