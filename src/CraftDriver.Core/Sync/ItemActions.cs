using System.Net;
using CraftDriver.Core.Config;
using CraftDriver.Core.Graph;
using CraftDriver.Core.State;
using CraftDriver.Core.Vfs;

namespace CraftDriver.Core.Sync;

/// <summary>Erro de ação pedida pelo usuário (menu/CLI/janela) com mensagem pronta para mostrar.</summary>
public sealed class ItemActionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Resultado de "copiar link": <see cref="FellBackToOrganization"/> quando a política do tenant recusou "qualquer pessoa".</summary>
public sealed record ShareResult(string Url, string Type, string Scope, bool FellBackToOrganization);

/// <summary>
/// Ações sob demanda sobre um item da nuvem: link de compartilhamento, versões anteriores e Lixeira.
/// Ficam fora do laço de sincronização: falam direto com a Graph da drive e só ajustam o estado local
/// para que o motor (delta/cache) enxergue o resultado.
/// </summary>
public sealed class ItemActions(SyncEngine engine, VirtualFs fs)
{
    StateDb Db => engine.Db;

    (Item item, DriveSync drive) Remote(long lid, bool fileOnly)
    {
        var item = Db.Get(lid) ?? throw new ItemActionException("O item não existe mais.");
        var drive = engine.GetDrive(item.DriveId) ?? throw new ItemActionException("A drive deste item não está ativa.");
        if (item.LocalOnly)
            throw new ItemActionException($"\"{item.Name}\" existe só neste dispositivo (nome inválido no OneDrive ou arquivo temporário) e não é sincronizado.");
        if (item.ItemId == null)
            throw new ItemActionException($"\"{item.Name}\" ainda não foi enviado para a nuvem. Espere o envio terminar e tente de novo.");
        if (fileOnly && item.IsDir)
            throw new ItemActionException($"\"{item.Name}\" é uma pasta; versões anteriores existem só para arquivos.");
        return (item, drive);
    }

    static ItemActionException Friendly(string what, Exception ex) => ex switch
    {
        ItemActionException i => i,
        GraphException { Status: HttpStatusCode.NotFound } => new($"{what}: o item não foi encontrado na nuvem (excluído ou movido em outro lugar).", ex),
        GraphException { Status: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } g => new($"{what}: sem permissão ({g.Message}).", ex),
        GraphException g => new($"{what}: {g.Message}", ex),
        Auth.AuthRequiredException => new($"{what}: entre novamente na sua conta.", ex),
        _ when Errors.IsNetworkError(ex) => new($"{what}: sem conexão com a nuvem.", ex),
        _ => new($"{what}: {ex.Message}", ex),
    };

    // ---------- link de compartilhamento ----------

    /// <summary>
    /// Cria (ou reaproveita — a Graph devolve o link existente) um link de compartilhamento. Tenta primeiro
    /// "qualquer pessoa com o link" (anonymous); em conta corporativa/escolar cuja política proíbe esse tipo,
    /// cai para "pessoas da organização". No OneDrive pessoal "organization" não existe: o erro é repassado.
    /// </summary>
    public async Task<ShareResult> CreateShareLinkAsync(long lid, bool edit, CancellationToken ct)
    {
        var (item, drive) = Remote(lid, fileOnly: false);
        var type = edit ? "edit" : "view";
        try
        {
            try
            {
                var link = await drive.Api.CreateLinkAsync(drive.DriveId, item.ItemId!, type, "anonymous", ct);
                return new ShareResult(link.WebUrl, link.Type, link.Scope, false);
            }
            catch (GraphException g) when (drive.Config.Kind != "personal" && IsPolicyRefusal(g))
            {
                engine.Activity.Debug($"Link anônimo recusado para {item.Name} ({g.Message}); usando escopo da organização");
                var link = await drive.Api.CreateLinkAsync(drive.DriveId, item.ItemId!, type, "organization", ct);
                return new ShareResult(link.WebUrl, link.Type, link.Scope, true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Friendly("Não foi possível criar o link", ex);
        }
    }

    /// <summary>
    /// Recusa por política do tenant (links anônimos desligados pelo administrador). A Graph não documenta o
    /// código exato; na prática vem 403 (accessDenied) ou 400 (invalidRequest/notAllowed). 404, 401 e erros
    /// transitórios não contam.
    /// </summary>
    public static bool IsPolicyRefusal(GraphException g) =>
        g.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed;

    // ---------- versões ----------

    public async Task<IReadOnlyList<DriveItemVersion>> ListVersionsAsync(long lid, CancellationToken ct)
    {
        var (item, drive) = Remote(lid, fileOnly: true);
        try { return await drive.Api.ListVersionsAsync(drive.DriveId, item.ItemId!, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Friendly("Não foi possível listar as versões", ex);
        }
    }

    /// <summary>
    /// Torna <paramref name="versionId"/> a versão atual (a nuvem cria uma versão nova com aquele conteúdo).
    /// Recusa se o arquivo tem alterações locais não enviadas: restaurar por cima delas criaria um conflito
    /// e o usuário perderia a noção de qual conteúdo ficou valendo. Depois de restaurar, atualiza já os
    /// metadados locais (cTag nova) e descarta o cache, para nenhuma leitura servir o conteúdo velho.
    /// </summary>
    public async Task RestoreVersionAsync(long lid, string versionId, CancellationToken ct)
    {
        var (item, drive) = Remote(lid, fileOnly: true);
        if (item.Dirty || Db.PendingOpsFor(lid, OpKind.Upload))
            throw new ItemActionException(
                $"\"{item.Name}\" tem alterações feitas aqui que ainda não foram enviadas. Espere o envio terminar " +
                "(ou salve uma cópia do arquivo) antes de restaurar uma versão anterior.");
        RemoteItem? fresh;
        try
        {
            await drive.Api.RestoreVersionAsync(drive.DriveId, item.ItemId!, versionId, ct);
            fresh = await drive.Api.GetItemAsync(drive.DriveId, item.ItemId!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Friendly("Não foi possível restaurar a versão", ex);
        }

        var pinned = false;
        lock (Db.Lock)
        {
            var cur = Db.Get(lid);
            if (cur != null && !cur.Dirty)
            {
                if (fresh != null)
                {
                    cur.CTag = fresh.CTag ?? fresh.ETag;
                    cur.ETag = fresh.ETag;
                    cur.Hash = fresh.QuickXorHash;
                    cur.Size = fresh.Size;
                    cur.MTime = fresh.LastModified.ToUnixTimeMilliseconds();
                }
                else cur.CTag = null; // sem os metadados novos: força o download na próxima abertura
                if (cur.CachedCTag != null && !engine.IsOpen(lid))
                {
                    engine.Cache.Delete(cur.DriveId, lid);
                    cur.CachedCTag = null;
                }
                // aberto: o handle continua com o conteúdo antigo; a próxima abertura baixa (cTag diferente)
                Db.Update(cur);
                pinned = Db.IsEffectivelyPinned(lid);
            }
        }
        engine.Activity.Add(ActivityKind.Info, $"Versão anterior restaurada: {item.Name}");
        drive.Wake(); // delta confirma (e pega o resto que mudou)
        if (pinned) drive.WakePins(); // fixado: baixa já a versão restaurada
    }

    /// <summary>Nome da cópia: "relatorio (versão 2026-11-01 1030).docx" (hora local da versão).</summary>
    public static string VersionCopyName(string name, DateTimeOffset modified)
    {
        var ext = Path.GetExtension(name);
        if (ext.Length == name.Length) ext = ""; // ".bashrc": o nome inteiro, sem extensão
        var stem = name[..^ext.Length];
        return $"{stem} (versão {modified.ToLocalTime():yyyy-MM-dd HHmm}){ext}";
    }

    /// <summary>
    /// Baixa uma versão antiga como arquivo novo ao lado do original, sem tocar nele (o arquivo novo é enviado
    /// para a nuvem como qualquer outro). Devolve o nome criado.
    /// </summary>
    public async Task<string> DownloadVersionCopyAsync(long lid, DriveItemVersion version, CancellationToken ct)
    {
        var (item, drive) = Remote(lid, fileOnly: true);
        var dir = VirtualPathOf(item.ParentLid) ?? throw new ItemActionException("A pasta do arquivo não existe mais.");
        Directory.CreateDirectory(engine.Cache.TempDir);
        var temp = engine.Cache.NewTempFile();
        try
        {
            try
            {
                await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, true))
                    await drive.Api.DownloadVersionAsync(drive.DriveId, item.ItemId!, version.Id, dst, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw Friendly("Não foi possível baixar a versão", ex);
            }

            var wanted = VersionCopyName(item.Name, version.Modified);
            var name = wanted;
            ulong fh = 0;
            int r;
            for (var i = 2; ; i++)
            {
                r = fs.Create(dir + "/" + name, OpenFlags.RdWr | OpenFlags.Creat | OpenFlags.Excl, out fh);
                if (r != -Errno.EEXIST) break;
                var ext = Path.GetExtension(wanted);
                name = $"{wanted[..^ext.Length]} ({i}){ext}";
            }
            if (r != 0) throw new ItemActionException($"Não foi possível criar \"{name}\" (erro {-r}).");
            try
            {
                await using var src = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
                var buf = new byte[1 << 20];
                long off = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    var w = fs.Write(fh, buf.AsSpan(0, n), off);
                    if (w != n) throw new ItemActionException($"Falha ao gravar \"{name}\" (erro {-w}).");
                    off += n;
                }
            }
            finally { fs.Release(fh); }
            engine.Activity.Add(ActivityKind.Info, $"Versão de {version.Modified.ToLocalTime():g} de {item.Name} salva como {name}");
            return name;
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    /// <summary>Caminho no <see cref="VirtualFs"/> ("/Nome da drive/pasta/…") de um item.</summary>
    string? VirtualPathOf(long lid)
    {
        var parts = new List<string>();
        var cur = Db.Get(lid);
        if (cur == null) return null;
        var drive = engine.GetDrive(cur.DriveId);
        if (drive == null) return null;
        while (cur != null && cur.Lid != drive.RootLid)
        {
            parts.Add(cur.Name);
            cur = Db.Get(cur.ParentLid);
        }
        if (cur == null) return null;
        parts.Add(drive.Config.DisplayName.Replace('/', '-'));
        parts.Reverse();
        return "/" + string.Join('/', parts);
    }

    // ---------- Lixeira ----------

    /// <summary>
    /// Restaurar da Lixeira pela API só existe na v1.0 para o OneDrive pessoal (driveItem: restore). Contas
    /// corporativas/escolares e bibliotecas do SharePoint: só beta (site recycleBin) — use <see cref="RecycleBinUrl"/>.
    /// </summary>
    public static bool CanRestoreFromTrash(DriveConfig d) => d.Kind == "personal";

    /// <summary>
    /// Página da Lixeira no navegador. SharePoint/OneDrive for Business: site + /_layouts/15/RecycleBin.aspx
    /// (o webUrl da drive é o da biblioteca, ex. …/personal/u_empresa_com/Documents); pessoal: onedrive.live.com.
    /// </summary>
    public static string RecycleBinUrl(DriveConfig d)
    {
        if (d.Kind == "personal" || string.IsNullOrEmpty(d.WebUrl) || !Uri.TryCreate(d.WebUrl, UriKind.Absolute, out var u)
            || !u.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            return "https://onedrive.live.com/?v=recyclebin";
        // /personal/{usuário}/…, /sites/{site}/…, /teams/{site}/…; senão é uma biblioteca do site raiz
        var seg = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var site = seg.Length >= 2 && seg[0].ToLowerInvariant() is "personal" or "sites" or "teams" ? $"/{seg[0]}/{seg[1]}" : "";
        return $"{u.Scheme}://{u.Host}{site}/_layouts/15/RecycleBin.aspx";
    }

    /// <summary>Tira um item da Lixeira (OneDrive pessoal) e pede um delta para ele voltar à pasta local.</summary>
    public async Task<string> RestoreFromTrashAsync(long trashId, CancellationToken ct)
    {
        var rec = Db.Trash().FirstOrDefault(t => t.Id == trashId) ?? throw new ItemActionException("Esse item não está mais na lista de excluídos.");
        var drive = engine.GetDrive(rec.DriveId) ?? throw new ItemActionException("A drive deste item não está ativa.");
        if (!CanRestoreFromTrash(drive.Config))
            throw new ItemActionException(
                $"Restaurar pela API só é possível no OneDrive pessoal. Abra a Lixeira de \"{drive.Config.DisplayName}\" no navegador: {RecycleBinUrl(drive.Config)}");
        try
        {
            await drive.Api.RestoreDeletedAsync(drive.DriveId, rec.ItemId, ct);
        }
        catch (GraphException g) when (g.Status == HttpStatusCode.NotFound)
        {
            Db.RemoveTrash(rec.Id);
            throw new ItemActionException($"\"{rec.Name}\" não está mais na Lixeira (já restaurado ou excluído definitivamente).", g);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Friendly("Não foi possível restaurar da Lixeira", ex);
        }
        Db.RemoveTrash(rec.Id);
        engine.Activity.Add(ActivityKind.Info, $"Restaurado da Lixeira: {rec.Name}");
        drive.Wake();
        return rec.Name;
    }
}
