using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using CraftDriver.Core.State;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Fuse;

/// <summary>
/// Ponte evento do motor → notificações do kernel. A assinatura só enfileira: o evento pode vir de handler FUSE ou
/// de transação sob Db.Lock. A thread própria confirma o estado depois da transação e solta todos os monitores
/// antes das chamadas nativas (o kernel pode precisar executar um handler para terminar a invalidação).
/// Cada instância pertence a UMA sessão; nunca acessa _fs/_inodes estáticos de uma montagem posterior.
/// </summary>
sealed class KernelNotifications
{
    const string Lib = "fuse3";
    [DllImport(Lib)] static extern int fuse_lowlevel_notify_inval_entry(IntPtr se, ulong parent, byte[] name, nuint namelen);
    [DllImport(Lib)] static extern int fuse_lowlevel_notify_delete(IntPtr se, ulong parent, ulong child, byte[] name, nuint namelen);
    [DllImport(Lib)] static extern int fuse_lowlevel_notify_inval_inode(IntPtr se, ulong ino, long off, long len);

    readonly VirtualFs _fs;
    readonly InodeTable _inodes;
    readonly IntPtr _session;
    readonly BlockingCollection<(Item? antes, Item? depois)> _fila = new();
    readonly Thread _thread;
    readonly object _gate = new();
    bool _encerrando;

    public KernelNotifications(VirtualFs fs, InodeTable inodes, IntPtr session)
    {
        _fs = fs;
        _inodes = inodes;
        _session = session;
        _thread = new Thread(Loop) { IsBackground = true, Name = "fuse-notify" };
        fs.ItemChanged += Enfileirar;
        _thread.Start();
    }

    void Enfileirar(Item? antes, Item? depois)
    {
        lock (_gate)
            if (!_encerrando) _fila.Add((antes, depois));
    }

    void Loop()
    {
        foreach (var (antes, depois) in _fila.GetConsumingEnumerable())
        {
            try { Notificar(antes, depois); }
            catch (Exception e) { FuseHost.Log?.Invoke($"FUSE invalidação: {e.Message}"); }
        }
    }

    void Notificar(Item? antes, Item? depois)
    {
        var lid = (antes ?? depois)!.Lid;
        var atual = _fs.ItemOfLid(lid); // espera a transação soltar Db.Lock; rollback também é respeitado
        var ino = (ulong)lid + 1;
        var antigo = _inodes.PathOf(ino);
        var novo = atual == null ? null : _fs.PathOfLid(lid);
        var mudouNome = antigo != null && antigo != novo;
        if (mudouNome)
        {
            if (novo == null) _inodes.Detach(antigo!);
            else _inodes.Relocate(ino, novo);
            Entrada(antigo!, ino, excluir: true);
        }
        // Um nome que estava em cache negativo passa a existir mesmo se o inode nunca foi consultado.
        if (novo != null && (antes == null || mudouNome || antes.Name != atual!.Name || antes.ParentLid != atual.ParentLid))
            Entrada(novo, ino, excluir: false);
        if (_inodes.Has(ino) && (atual == null || antes == null || antes.CTag != atual.CTag || antes.MTime != atual.MTime || antes.Size != atual.Size))
            Conferir(fuse_lowlevel_notify_inval_inode(_session, ino, 0, 0));
    }

    void Entrada(string path, ulong ino, bool excluir)
    {
        var i = path.LastIndexOf('/');
        var parent = _inodes.Known(i <= 0 ? "/" : path[..i]);
        if (parent == 0xffffffff) return; // o kernel não conhece este pai: não há dentry em cache
        var nome = Encoding.UTF8.GetBytes(path[(i + 1)..] + "\0");
        var n = (nuint)(nome.Length - 1);
        Conferir(excluir ? fuse_lowlevel_notify_delete(_session, parent, ino, nome, n)
            : fuse_lowlevel_notify_inval_entry(_session, parent, nome, n));
    }

    static void Conferir(int r)
    {
        if (r != 0 && r != -Errno.ENOENT) FuseHost.Log?.Invoke($"FUSE invalidação retornou errno {-r}");
    }

    /// <summary>Desinscreve e junta a thread ANTES de destruir a sessão. Se não sair, a sessão deve ser vazada.</summary>
    public bool Stop()
    {
        _fs.ItemChanged -= Enfileirar;
        lock (_gate) { _encerrando = true; _fila.CompleteAdding(); }
        return _thread.Join(TimeSpan.FromSeconds(15));
    }
}
