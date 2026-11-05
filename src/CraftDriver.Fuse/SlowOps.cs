using System.Collections.Concurrent;

namespace CraftDriver.Fuse;

/// <summary>
/// Threads próprias para operações do FUSE que podem esperar a rede (open de arquivo só-na-nuvem, truncate,
/// rename, fsync). Elas bloqueiam esperando o download — no ThreadPool do .NET isso o deixaria sem threads
/// justamente para o download assíncrono que estão esperando. Cresce sob demanda (uma thread ociosa é
/// reaproveitada; sem ociosa, cria outra) e cada thread sai depois de 30 s sem trabalho. Acima de
/// <see cref="MaxThreads"/> o trabalho espera na fila, sem ocupar o loop do FUSE.
/// </summary>
public static class SlowOps
{
    public const int MaxThreads = 256;
    static readonly ConcurrentQueue<Action> Queue = new();
    static readonly SemaphoreSlim Signal = new(0);
    static int _threads, _idle;

    public static int Threads => Volatile.Read(ref _threads);

    /// <summary>
    /// Enfileira e garante que alguém vai pegar: reserva uma thread ociosa (decrementando o contador e acordando
    /// uma) ou cria outra. Sem reserva, dois trabalhos chegando juntos podiam acordar a mesma thread, e o segundo
    /// open esperaria o download do primeiro.
    /// </summary>
    public static void Run(Action work)
    {
        Queue.Enqueue(work);
        while (true)
        {
            var idle = Volatile.Read(ref _idle);
            if (idle == 0) break;
            if (Interlocked.CompareExchange(ref _idle, idle - 1, idle) == idle)
            {
                Signal.Release();
                return;
            }
        }
        if (Interlocked.Increment(ref _threads) > MaxThreads)
        {
            Interlocked.Decrement(ref _threads); // no limite: fica na fila; quem terminar esvazia a fila antes de ficar ocioso
            return;
        }
        new Thread(Worker) { IsBackground = true, Name = "fuse-slow" }.Start();
    }

    static void Worker()
    {
        try
        {
            while (true)
            {
                while (Queue.TryDequeue(out var work))
                {
                    try { work(); }
                    catch { /* o FuseHost já trata e responde; uma exceção aqui não pode matar a thread */ }
                }
                Interlocked.Increment(ref _idle);
                if (Signal.Wait(TimeSpan.FromSeconds(30))) continue; // quem acordou já descontou o ocioso
                // 30 s sem trabalho: sai, a menos que alguém tenha acabado de reservar esta thread
                var left = false;
                while (true)
                {
                    var idle = Volatile.Read(ref _idle);
                    if (idle == 0) break; // reservada: o sinal está a caminho
                    if (Interlocked.CompareExchange(ref _idle, idle - 1, idle) == idle) { left = true; break; }
                }
                if (left) return;
                Signal.Wait();
            }
        }
        finally { Interlocked.Decrement(ref _threads); }
    }
}
