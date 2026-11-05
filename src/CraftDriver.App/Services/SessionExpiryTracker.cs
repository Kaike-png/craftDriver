namespace CraftDriver.App.Services;

/// <summary>
/// Decide quando avisar que a sessão de uma conta expirou: uma vez por expiração, não a cada ciclo de
/// sincronização. A conta volta a poder ser avisada depois que nenhuma drive dela pede login (entrou de
/// novo) ou depois que ela some da lista (removida).
/// </summary>
public sealed class SessionExpiryTracker
{
    readonly HashSet<string> _notified = [];
    readonly Lock _lock = new();

    /// <summary>Estado atual de cada drive; devolve as contas que acabaram de passar a pedir login.</summary>
    public IReadOnlyList<string> Update(IEnumerable<(string AccountId, bool AuthRequired)> drives)
    {
        var expired = drives.GroupBy(d => d.AccountId)
            .Where(g => g.Any(d => d.AuthRequired))
            .Select(g => g.Key)
            .ToHashSet();
        lock (_lock)
        {
            _notified.IntersectWith(expired);
            var fresh = expired.Where(_notified.Add).ToList();
            return fresh;
        }
    }
}
