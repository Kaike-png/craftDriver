namespace CraftDriver.Core.Graph;

/// <summary>Onde o envio guarda a URL da sessão de upload para retomá-la (null = sessão nova / encerrada).</summary>
public interface IUploadSessionStore
{
    string? Url { get; }
    void Save(string? url);
    /// <summary>O envio retomou a sessão a partir de <paramref name="offset"/> bytes (no início do envio ou depois de um
    /// 416 recusar uma parte já recebida). O GraphClient não registra nada; quem guarda a sessão decide (o motor loga em Debug).</summary>
    void Resumed(long offset) { }
}

/// <summary>
/// Operações de arquivo da Microsoft Graph usadas pelo motor de sincronização.
/// Existe uma implementação real (<see cref="GraphClient"/>) e uma falsa em memória nos testes.
/// </summary>
public interface IGraphApi
{
    Task<GraphDrive> GetDriveAsync(string driveId, CancellationToken ct);
    /// <summary>Configura a banda agregada de uploads da drive (0 = ilimitada); opcional para implementações locais.</summary>
    void ConfigureUploadBandwidth(string driveId, long bytesPerSecond) { }
    Task<RemoteItem> GetRootAsync(string driveId, CancellationToken ct);
    Task<RemoteItem?> GetItemAsync(string driveId, string itemId, CancellationToken ct);
    Task<RemoteItem?> GetChildByNameAsync(string driveId, string parentId, string name, CancellationToken ct);

    /// <param name="link">null = enumeração completa; senão o nextLink/deltaLink anterior.</param>
    Task<DeltaPage> GetDeltaAsync(string driveId, string? link, CancellationToken ct);

    /// <summary>
    /// Baixa o conteúdo para <paramref name="destination"/> (gravável e posicionável). Com
    /// <paramref name="offset"/> &gt; 0 pede só o restante (Range) e acrescenta; se o servidor mandar o arquivo
    /// inteiro, recomeça o destino do zero. <paramref name="progress"/> recebe o total já no destino.
    /// </summary>
    Task DownloadAsync(string driveId, string itemId, Stream destination, long offset, Action<long>? progress, CancellationToken ct);

    /// <summary>
    /// Baixa só a faixa [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>) do conteúdo
    /// (Range bytes=a-b) e grava exatamente esses bytes na posição atual de <paramref name="destination"/>. Falha
    /// (sem gravar conteúdo de outra faixa) se o servidor não respeitar o Range ou entregar menos bytes.
    /// <paramref name="progress"/> recebe os bytes já recebidos desta faixa. Usado na leitura por faixas (streaming).
    /// </summary>
    Task DownloadRangeAsync(string driveId, string itemId, Stream destination, long offset, long length, Action<long>? progress, CancellationToken ct)
        => throw new NotSupportedException("Leitura por faixas não suportada por esta implementação");

    /// <summary>
    /// Envia conteúdo. Com <paramref name="itemId"/> substitui o arquivo existente (com If-Match
    /// <paramref name="ifMatchETag"/> se informado); sem ele cria um novo filho de <paramref name="parentId"/>
    /// e falha com 409 se já existir um item com esse nome.
    /// </summary>
    /// <param name="session">Persistência da sessão de upload (arquivos grandes): retoma de onde parou.</param>
    Task<RemoteItem> UploadAsync(string driveId, string parentId, string name, string? itemId, string? ifMatchETag,
        string localFile, DateTimeOffset modified, Action<long>? progress, CancellationToken ct, IUploadSessionStore? session = null);

    Task<RemoteItem> CreateFolderAsync(string driveId, string parentId, string name, CancellationToken ct);
    Task DeleteAsync(string driveId, string itemId, CancellationToken ct);
    /// <summary>Move/renomeia; falha com 409 se já houver item com o nome no destino (ver a sobrecarga com <c>replace</c>).</summary>
    Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct);
    Task<RemoteItem> SetModifiedAsync(string driveId, string itemId, DateTimeOffset modified, CancellationToken ct);

    // ---------- compartilhados comigo / atalhos (itens de outras drives) ----------

    /// <summary>
    /// Itens compartilhados com o dono da drive (GET /me/drive/sharedWithMe). Cada um vem com
    /// <see cref="RemoteItem.RemoteDriveId"/>/<see cref="RemoteItem.RemoteId"/> preenchidos.
    /// A API está obsoleta ("degraded") na Microsoft: pode voltar incompleta ou vazia.
    /// </summary>
    Task<List<RemoteItem>> GetSharedWithMeAsync(CancellationToken ct) => Task.FromResult(new List<RemoteItem>());

    /// <summary>
    /// Uma página dos filhos de uma pasta (GET /drives/{d}/items/{id}/children). <paramref name="next"/> é o
    /// nextLink anterior (null = primeira página). Usado nas pastas de outras drives, onde não há delta.
    /// </summary>
    Task<DeltaPage> GetChildrenAsync(string driveId, string itemId, string? next, CancellationToken ct) =>
        throw new NotSupportedException("listagem de filhos não implementada");

    /// <summary>
    /// O usuário atual pode alterar o item? (GET .../permissions: para quem não é dono, só as permissões que
    /// valem para ele; papel write/owner = sim). Sem como saber, devolve false (somente leitura).
    /// </summary>
    Task<bool> CanWriteAsync(string driveId, string itemId, CancellationToken ct) => Task.FromResult(false);

    // ---------- compartilhamento, versões e Lixeira (v1.0) ----------

    /// <summary>POST /drives/{d}/items/{id}/createLink. <paramref name="type"/> view|edit; <paramref name="scope"/> anonymous|organization.</summary>
    Task<SharingLink> CreateLinkAsync(string driveId, string itemId, string type, string scope, CancellationToken ct) =>
        throw new NotSupportedException("Links de compartilhamento não são suportados por esta implementação.");

    /// <summary>GET /drives/{d}/items/{id}/versions (da mais nova — a atual — para a mais antiga).</summary>
    Task<IReadOnlyList<DriveItemVersion>> ListVersionsAsync(string driveId, string itemId, CancellationToken ct) =>
        throw new NotSupportedException("Versões não são suportadas por esta implementação.");

    /// <summary>POST /drives/{d}/items/{id}/versions/{v}/restoreVersion (cria uma versão nova com o conteúdo da antiga).</summary>
    Task RestoreVersionAsync(string driveId, string itemId, string versionId, CancellationToken ct) =>
        throw new NotSupportedException("Versões não são suportadas por esta implementação.");

    /// <summary>GET /drives/{d}/items/{id}/versions/{v}/content para <paramref name="destination"/> (do início).</summary>
    Task DownloadVersionAsync(string driveId, string itemId, string versionId, Stream destination, CancellationToken ct) =>
        throw new NotSupportedException("Versões não são suportadas por esta implementação.");

    /// <summary>
    /// POST /me/drive/items/{id}/restore: tira da Lixeira um item excluído. Na v1.0 só existe para o OneDrive
    /// pessoal (contas corporativas/SharePoint: só beta, então o CraftDriver abre a Lixeira no navegador).
    /// </summary>
    Task<RemoteItem> RestoreDeletedAsync(string driveId, string itemId, CancellationToken ct) =>
        throw new NotSupportedException("Restaurar da Lixeira não é suportado por esta implementação.");

    // ---------- conflitos sem perda (PENDENCIAS §12/§13) ----------

    /// <summary>
    /// Move/renomeia com <c>conflictBehavior</c> explícito: <paramref name="replace"/> = substitui o que houver com esse nome
    /// no destino (só quando o usuário sobrescreveu um item que conhecia); senão 409.
    /// </summary>
    Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, bool replace, CancellationToken ct) =>
        MoveAsync(driveId, itemId, newParentId, newName, ct);

    /// <summary>Exclui; com <paramref name="ifMatchETag"/> a nuvem recusa (412) se o item mudou depois desse eTag.</summary>
    Task DeleteAsync(string driveId, string itemId, string? ifMatchETag, CancellationToken ct) => DeleteAsync(driveId, itemId, ct);

    /// <summary>
    /// Delta de uma pasta (GET /drives/{d}/items/{id}/delta): só existe no OneDrive pessoal. <paramref name="link"/> é o
    /// nextLink/deltaLink anterior (null = enumeração completa da pasta). Sem suporte: 400/404 ou NotSupportedException.
    /// </summary>
    Task<DeltaPage> GetFolderDeltaAsync(string driveId, string itemId, string? link, CancellationToken ct) =>
        throw new NotSupportedException("Delta por pasta não suportado por esta implementação.");
}
