namespace CraftDriver.Core.Auth;

/// <summary>
/// Access token por conta guardado em memória até perto de expirar. Sem isso cada pedido à Graph (um bloco de
/// streaming, uma parte de upload) chamava AcquireTokenSilent, e o MsalCacheHelper relia o chaveiro (D-Bus/libsecret)
/// a cada vez. Pedidos simultâneos de uma conta sem token válido esperam uma única aquisição. 401 do servidor pede
/// renovação forçada (o <see cref="Graph.GraphClient"/> já repete o pedido com forceRefresh).
/// </summary>
public sealed class AccessTokenCache
{
    /// <summary>Renova antes de faltar isso para expirar (relógio torto, pedido longo).</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    sealed record Snapshot(string Token, DateTimeOffset ExpiresOn);

    sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile Snapshot? Current;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Relógio (testes trocam).</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <param name="acquire">(forçarRenovação, ct) → token e expiração, vindo do MSAL.</param>
    public async Task<string> GetAsync(string account, bool forceRefresh,
        Func<bool, CancellationToken, Task<(string Token, DateTimeOffset ExpiresOn)>> acquire, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(account, _ => new Entry());
        var seen = entry.Current;
        if (!forceRefresh && Fresh(seen)) return seen!.Token;
        await entry.Gate.WaitAsync(ct);
        try
        {
            // outro pedido renovou enquanto este esperava a vez (inclusive a renovação forçada por um 401 simultâneo)
            var cur = entry.Current;
            if (Fresh(cur) && (!forceRefresh || !ReferenceEquals(cur, seen))) return cur!.Token;
            var (token, expiresOn) = await acquire(forceRefresh, ct);
            entry.Current = new Snapshot(token, expiresOn);
            return token;
        }
        finally { entry.Gate.Release(); }
    }

    bool Fresh(Snapshot? s) => s != null && s.ExpiresOn - Now() > RefreshMargin;

    /// <summary>Esquece o token da conta (saiu da conta).</summary>
    public void Invalidate(string account) => _entries.TryRemove(account, out _);
}
