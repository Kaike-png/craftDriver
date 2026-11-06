using System.Diagnostics;

namespace CraftDriver.Core.Graph;

/// <summary>
/// "Throttled até T" compartilhado por conta: um 429/503 com Retry-After vale para todos os pedidos da conta (delta,
/// downloads, blocos de streaming, fila de envios), não só para o pedido que o recebeu — continuar batendo na janela
/// faz a Microsoft estender o bloqueio. Um objeto por conta; os <see cref="GraphClient"/> das drives da conta o recebem.
/// </summary>
public sealed class GraphThrottle
{
    /// <summary>Horário (Stopwatch) até o qual a conta não deve mandar pedidos; 0 = livre.</summary>
    long _until;

    /// <summary>Tempo que ainda falta para a conta poder voltar a mandar pedidos.</summary>
    public TimeSpan Remaining =>
        Interlocked.Read(ref _until) is var until && until > Stopwatch.GetTimestamp()
            ? Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), until)
            : TimeSpan.Zero;

    /// <summary>Servidor pediu para esperar <paramref name="wait"/>: estende o bloqueio da conta (nunca encurta).</summary>
    public void Block(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero) return;
        var until = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        for (var cur = Interlocked.Read(ref _until); cur < until; cur = Interlocked.Read(ref _until))
            if (Interlocked.CompareExchange(ref _until, until, cur) == cur) return;
    }

    /// <summary>Espera o fim do bloqueio (se houver) antes de mandar um pedido.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        // em laço: outro pedido pode ter estendido o bloqueio enquanto este esperava
        while (Remaining is var left && left > TimeSpan.Zero)
            await Task.Delay(left, ct);
    }
}
