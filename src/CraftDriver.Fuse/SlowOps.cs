using System.Collections.Concurrent;

namespace CraftDriver.Fuse;

/// <summary>
/// Operações que não devem ocupar o loop FUSE. Duas filas com threads próprias: rede (até 256 esperas) e local
/// (até 16 operações em disco). Uma fila única invertia a prioridade: 256 reads presos na rede impediam fsync,
/// rename e open de arquivos já no cache. Não usar ThreadPool: bloquear nele também impede o download assíncrono.
/// </summary>
public static class SlowOps
{
    public const int MaxThreads = 256;
    const int MaxLocalThreads = 16;
    static readonly Fila Rede = new(MaxThreads, "fuse-rede");
    static readonly Fila Local = new(MaxLocalThreads, "fuse-local");

    public static int Threads => Rede.Threads + Local.Threads;

    public static void Run(Action work, bool network = false) => (network ? Rede : Local).Run(work);

    sealed class Fila(int limite, string nome)
    {
        readonly ConcurrentQueue<Action> _queue = new();
        readonly SemaphoreSlim _signal = new(0);
        int _threads, _idle;

        public int Threads => Volatile.Read(ref _threads);

        public void Run(Action work)
        {
            _queue.Enqueue(work);
            while (true)
            {
                var idle = Volatile.Read(ref _idle);
                if (idle == 0) break;
                if (Interlocked.CompareExchange(ref _idle, idle - 1, idle) == idle)
                {
                    _signal.Release();
                    return;
                }
            }
            if (Interlocked.Increment(ref _threads) > limite)
            {
                Interlocked.Decrement(ref _threads);
                return;
            }
            new Thread(Worker) { IsBackground = true, Name = nome }.Start();
        }

        void Worker()
        {
            try
            {
                while (true)
                {
                    while (_queue.TryDequeue(out var work))
                    {
                        try { work(); }
                        catch { /* FuseHost já responde os erros; um trabalho não pode matar a fila */ }
                    }
                    Interlocked.Increment(ref _idle);
                    if (_signal.Wait(TimeSpan.FromSeconds(30))) continue;
                    var left = false;
                    while (true)
                    {
                        var idle = Volatile.Read(ref _idle);
                        if (idle == 0) break;
                        if (Interlocked.CompareExchange(ref _idle, idle - 1, idle) == idle) { left = true; break; }
                    }
                    if (left) return;
                    _signal.Wait();
                }
            }
            finally { Interlocked.Decrement(ref _threads); }
        }
    }
}
