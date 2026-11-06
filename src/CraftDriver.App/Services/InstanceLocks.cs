namespace CraftDriver.App.Services;

/// <summary>
/// Trava do ponto de montagem do processo (além da trava da configuração, <see cref="CraftDriver.Core.Config.AppPaths.LockFile"/>):
/// chaveada pelo caminho canônico da pasta sincronizada, no runtime do usuário. Duas instâncias com
/// CRAFTDRIVER_HOME/XDG_RUNTIME_DIR de configuração diferentes e a mesma pasta montavam por cima uma da outra.
/// Ao trocar a pasta pelas Configurações, a trava passa para a nova antes de desmontar a antiga.
/// </summary>
public static class InstanceLocks
{
    static readonly Lock Gate = new();
    static SingleInstance? _mount;
    static string? _mountKey;

    /// <summary>Há trava de montagem neste processo (só o app de verdade; testes com AppHost não pegam).</summary>
    public static bool Active { get { lock (Gate) return _mount != null; } }

    /// <summary>Caminho canônico que a trava protege agora.</summary>
    public static string? MountKey { get { lock (Gate) return _mountKey; } }

    /// <summary>Texto para quando outra instância já usa a pasta.</summary>
    public static string InUseText(string canonical) =>
        $"Outra instância do craftDriver (com outra configuração) já usa a pasta {canonical}. Encerre-a antes ou escolha outra pasta.";

    /// <summary>Pega a trava da pasta; devolve o motivo da recusa (outra instância, erro do sistema) ou null.</summary>
    /// <param name="inUse">true quando a recusa é por outra instância (não erro do sistema).</param>
    public static string? AcquireMount(string mountPoint, out bool inUse)
    {
        inUse = false;
        var key = MountPath.Canonical(mountPoint).TrimEnd('/');
        lock (Gate)
        {
            if (_mount != null && _mountKey == key) return null;
            SingleInstance? got;
            try { got = SingleInstance.TryAcquire(SingleInstance.MountLockPath(key)); }
            catch (SingleInstanceException ex) { return ex.Message; }
            if (got == null)
            {
                inUse = true;
                return InUseText(key);
            }
            _mount?.Dispose();
            _mount = got;
            _mountKey = key;
            return null;
        }
    }

    /// <summary>
    /// Troca a pasta: só se o processo já tem uma trava (app de verdade). Pega a nova mantendo a antiga até
    /// <see cref="CommitMove"/>/<see cref="CancelMove"/>.
    /// </summary>
    public static string? PrepareMove(string newMountPoint, out SingleInstance? pending, out string? pendingKey)
    {
        pending = null;
        pendingKey = null;
        lock (Gate)
        {
            if (_mount == null) return null;
            var key = MountPath.Canonical(newMountPoint).TrimEnd('/');
            if (key == _mountKey) return null;
            try { pending = SingleInstance.TryAcquire(SingleInstance.MountLockPath(key)); }
            catch (SingleInstanceException ex) { return ex.Message; }
            if (pending == null) return InUseText(key);
            pendingKey = key;
            return null;
        }
    }

    public static void CommitMove(SingleInstance? pending, string? pendingKey)
    {
        if (pending == null) return;
        lock (Gate)
        {
            _mount?.Dispose();
            _mount = pending;
            _mountKey = pendingKey;
        }
    }

    public static void CancelMove(SingleInstance? pending) => pending?.Dispose();

    public static void Release()
    {
        lock (Gate)
        {
            _mount?.Dispose();
            _mount = null;
            _mountKey = null;
        }
    }
}
