namespace CraftDriver.Core.Sync;

/// <summary>Limite de concorrência que pode mudar com gente esperando (configurações aplicadas sem reiniciar).</summary>
public sealed class AdjustableLimiter(int limit)
{
    readonly object _lock = new();
    readonly LinkedList<TaskCompletionSource> _waiters = new();
    int _limit = Math.Max(1, limit), _active;

    public int Limit
    {
        get { lock (_lock) return _limit; }
    }

    public Task WaitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskCompletionSource tcs;
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (_active < _limit)
            {
                _active++;
                return Task.CompletedTask;
            }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(tcs);
        }
        if (!ct.CanBeCanceled) return tcs.Task;
        var reg = ct.Register(() =>
        {
            lock (_lock)
            {
                // ainda na fila: sai sem ocupar vaga; se a vaga já foi dada, quem esperava a usa normalmente
                if (node.List == null) return;
                _waiters.Remove(node);
            }
            tcs.TrySetCanceled(ct);
        });
        return tcs.Task.ContinueWith(t => { reg.Dispose(); return t; }, TaskScheduler.Default).Unwrap();
    }

    public void Release()
    {
        lock (_lock)
        {
            _active--;
            Pump();
        }
    }

    /// <summary>Muda o limite. Aumentar libera quem espera na hora; diminuir vale conforme as atuais terminam.</summary>
    public void SetLimit(int limit)
    {
        lock (_lock)
        {
            _limit = Math.Max(1, limit);
            Pump();
        }
    }

    void Pump()
    {
        while (_active < _limit && _waiters.First is { } first)
        {
            _waiters.RemoveFirst();
            _active++;
            first.Value.TrySetResult();
        }
    }
}
