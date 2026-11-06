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

        /// <summary>Deixa de contar como ociosa; false se um <see cref="Run"/> já reservou esta vaga (e vai sinalizar).</summary>
        bool SairDeOciosa()
        {
            while (true)
            {
                var idle = Volatile.Read(ref _idle);
                if (idle == 0) return false;
                if (Interlocked.CompareExchange(ref _idle, idle - 1, idle) == idle) return true;
            }
        }

        void Worker()
        {
            while (true)
            {
                while (_queue.TryDequeue(out var work))
                {
                    try { work(); }
                    catch { /* FuseHost já responde os erros; um trabalho não pode matar a fila */ }
                }
                Interlocked.Increment(ref _idle);
                // Um Run pode ter enfileirado entre o TryDequeue que falhou e o _idle++: viu idle==0 e o limite cheio
                // (esta thread conta em _threads) e voltou sem acordar ninguém. Reler a fila fecha a janela.
                if (!_queue.IsEmpty && SairDeOciosa()) continue;
                if (_signal.Wait(TimeSpan.FromSeconds(30))) continue;
                if (!SairDeOciosa())
                {
                    _signal.Wait(); // um Run reservou esta thread no limite do prazo: o sinal está a caminho
                    continue;
                }
                // Encerrando: sai de _threads ANTES de reler a fila — um Run que viu idle==0 com esta thread ainda
                // contada também bateu no limite e não criou outra. Se sobrou trabalho, volta (se couber no limite).
                Interlocked.Decrement(ref _threads);
                if (_queue.IsEmpty) return;
                if (Interlocked.Increment(ref _threads) > limite)
                {
                    Interlocked.Decrement(ref _threads); // outra thread nova já pegou a vaga e vai drenar a fila
                    return;
                }
            }
        }
    }
}
