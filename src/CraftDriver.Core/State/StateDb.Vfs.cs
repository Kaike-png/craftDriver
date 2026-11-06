namespace CraftDriver.Core.State;

public sealed partial class StateDb
{
    /// <summary>
    /// Mudança de item. Pode disparar sob Lock/transação: consumidores só enfileiram e consultam o estado confirmado
    /// em outra thread. Nunca chamar o kernel neste evento (um handler FUSE pode estar esperando o mesmo monitor).
    /// </summary>
    public event Action<Item?, Item?>? ItemChanged;

    [ThreadStatic] static int _mutacaoLocal;
    bool DeveNotificar => _mutacaoLocal == 0 && ItemChanged != null;

    /// <summary>O kernel já atualiza seu cache em resposta a mutações locais; não refletir essas mudanças de volta.</summary>
    public readonly struct EscopoLocal : IDisposable
    {
        internal EscopoLocal(bool _) { _mutacaoLocal++; }
        public void Dispose() => _mutacaoLocal--;
    }

    public EscopoLocal MutacaoLocal() => new(true);

    void AvisarItem(Item? antes, Item? depois)
    {
        if (DeveNotificar) ItemChanged?.Invoke(antes, depois?.Clone());
    }

    // Precisão POSIX complementar à data em ms da Graph, sem mudar o contrato Item.MTime.
    // O ms é guardado junto para descartar a fração se o delta substituir a data.
    readonly Dictionary<long, (long ms, int nanos)> _precisaoMtime = new();

    static string ChaveNome(string nome)
    {
        try { return nome.Normalize().ToUpperInvariant(); }
        catch (ArgumentException) { return nome.ToUpperInvariant(); }
    }

    void InicializarVfs()
    {
        try { Exec("ALTER TABLE items ADD COLUMN name_key TEXT"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* migração já aplicada */ }
        // Só a migração lê nomes antigos; Insert/Update mantêm a chave daqui em diante.
        foreach (var item in Items($"SELECT {Cols} FROM items WHERE name_key IS NULL"))
            Exec("UPDATE items SET name_key=$k WHERE lid=$l", ("$k", ChaveNome(item.Name)), ("$l", item.Lid));
        Exec("CREATE INDEX IF NOT EXISTS ix_items_name_key ON items(parent_lid,name_key)");
        Exec("CREATE TABLE IF NOT EXISTS vfs_mtime(lid INTEGER PRIMARY KEY, ms INTEGER NOT NULL, nanos INTEGER NOT NULL)");
        using var cmd = Cmd("SELECT lid,ms,nanos FROM vfs_mtime", []);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) _precisaoMtime[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt32(2));
    }

    long _pinnedVersion = -1;
    HashSet<long> _pinnedVfs = [];

    /// <summary>Conjunto efetivo de fixados, reconstruído uma vez por geração; xattrs não fazem uma CTE por irmão.</summary>
    public bool IsEffectivelyPinnedVfs(long lid)
    {
        lock (Lock)
        {
            if (_pinnedVersion != _geracaoFixados)
            {
                using var cmd = Cmd("""
                    WITH RECURSIVE e(lid) AS (
                      SELECT lid FROM excluded
                      UNION SELECT i.lid FROM items i JOIN e ON i.parent_lid=e.lid),
                    p(lid) AS (
                      SELECT lid FROM items WHERE pinned=1 AND lid NOT IN (SELECT lid FROM e)
                      UNION SELECT i.lid FROM items i JOIN p ON i.parent_lid=p.lid WHERE i.lid NOT IN (SELECT lid FROM e))
                    SELECT lid FROM p
                    """, []);
                using var r = cmd.ExecuteReader();
                var set = new HashSet<long>();
                while (r.Read()) set.Add(r.GetInt64(0));
                _pinnedVfs = set;
                _pinnedVersion = _geracaoFixados;
            }
            return _pinnedVfs.Contains(lid);
        }
    }

    public int FracaoMtime(long lid, long ms)
    {
        lock (Lock) return _precisaoMtime.TryGetValue(lid, out var valor) && valor.ms == ms ? valor.nanos : 0;
    }

    /// <summary>Resto de nanossegundos abaixo de 1 ms (0..999999). Zero remove o complemento persistido.</summary>
    public void DefinirFracaoMtime(long lid, long ms, int nanos)
    {
        lock (Lock)
        {
            if (nanos == 0)
            {
                if (!_precisaoMtime.Remove(lid)) return;
                Exec("DELETE FROM vfs_mtime WHERE lid=$l", ("$l", lid));
            }
            else
            {
                _precisaoMtime[lid] = (ms, nanos);
                Exec("INSERT OR REPLACE INTO vfs_mtime(lid,ms,nanos) VALUES($l,$m,$n)", ("$l", lid), ("$m", ms), ("$n", nanos));
            }
        }
    }

    /// <summary>
    /// Geração do conjunto de fixados (<see cref="IsEffectivelyPinnedVfs"/>): sobe com tudo que muda caminhos
    /// (<see cref="MudouCaminhos"/>) e também quando só a marca de fixação muda. Ler sob <see cref="Lock"/>.
    /// </summary>
    long _geracaoFixados;

    /// <summary>
    /// Inserção, mudança de pasta/nome/tipo/drive, exclusão seletiva ou rollback: invalida o cache de caminhos do
    /// FUSE (<see cref="ItemsVersion"/>) e o conjunto de fixados. Apagar um item NÃO precisa: o acerto do cache
    /// revalida o lid com Get (lid nunca é reaproveitado) e apagar uma pasta apaga a subárvore.
    /// </summary>
    void MudouCaminhos()
    {
        ItemsVersion++;
        _geracaoFixados++;
    }

    /// <summary>
    /// Confirmação do envio de um arquivo gravado aqui (§22.6): sai de Dirty e só ganha cTag/eTag/id da nuvem, com o
    /// mesmo tamanho e data locais. O kernel já tem exatamente esse conteúdo (foi ele que gravou): não notificar,
    /// senão o notify_inval_inode descarta as páginas e atributos do arquivo recém-gravado.
    /// </summary>
    static bool ConfirmacaoDeEnvio(Item antes, Item depois) =>
        antes.Dirty && !depois.Dirty && antes.ParentLid == depois.ParentLid && antes.Name == depois.Name
        && antes.Size == depois.Size && antes.MTime == depois.MTime;

    /// <summary>
    /// Blocos da leitura por faixas descartados (hash divergente): o kernel pode ter páginas desse conteúdo no
    /// cache (sem direct_io). Avisa como mudança de versão — mesmo dentro de um handler FUSE, já que o kernel não
    /// sabe do descarte — para o notify_inval_inode descartar as páginas.
    /// </summary>
    public void AvisarConteudoDescartado(long lid)
    {
        if (ItemChanged == null) return;
        Item? atual;
        lock (Lock) atual = Get(lid);
        if (atual == null) return;
        var antes = atual.Clone();
        antes.CTag = "\0descartado";
        ItemChanged?.Invoke(antes, atual);
    }
}
