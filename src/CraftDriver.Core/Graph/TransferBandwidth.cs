using System.Diagnostics;
using System.Net;

namespace CraftDriver.Core.Graph;

/// <summary>Orçamento agregado por drive/direção, sem crédito acumulado nem rajada inicial.</summary>
internal sealed class TransferBandwidth(long bytesPerSecond)
{
    readonly SemaphoreSlim _gate = new(1, 1);
    long _rate = bytesPerSecond;

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
        await _gate.WaitAsync(ct);
        try
        {
            // Tempo monotônico: Task.Delay pode arredondar frações de milissegundo para baixo.
            var duration = TimeSpan.FromSeconds(SecondsFor(bytes));
            var start = Stopwatch.GetTimestamp();
            while (duration - Stopwatch.GetElapsedTime(start) is var remaining && remaining > TimeSpan.Zero)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1, 60_000)), ct);
        }
        finally { _gate.Release(); }
    }
}

internal sealed class BandwidthContent(byte[] bytes, TransferBandwidth bandwidth) : HttpContent
{
    public double PacingSeconds => bandwidth.SecondsFor(bytes.Length);
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        using var destination = new BandwidthWriteStream(stream, bandwidth);
        await destination.WriteAsync(bytes.AsMemory(), ct);
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
