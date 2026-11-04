namespace CraftDriver.Core.Graph;

/// <summary>
/// Operações de arquivo da Microsoft Graph usadas pelo motor de sincronização.
/// Existe uma implementação real (<see cref="GraphClient"/>) e uma falsa em memória nos testes.
/// </summary>
public interface IGraphApi
{
    Task<GraphDrive> GetDriveAsync(string driveId, CancellationToken ct);
    Task<RemoteItem> GetRootAsync(string driveId, CancellationToken ct);
    Task<RemoteItem?> GetItemAsync(string driveId, string itemId, CancellationToken ct);
    Task<RemoteItem?> GetChildByNameAsync(string driveId, string parentId, string name, CancellationToken ct);

    /// <param name="link">null = enumeração completa; senão o nextLink/deltaLink anterior.</param>
    Task<DeltaPage> GetDeltaAsync(string driveId, string? link, CancellationToken ct);

    Task DownloadAsync(string driveId, string itemId, Stream destination, Action<long>? progress, CancellationToken ct);

    /// <summary>
    /// Envia conteúdo. Com <paramref name="itemId"/> substitui o arquivo existente (com If-Match
    /// <paramref name="ifMatchETag"/> se informado); sem ele cria um novo filho de <paramref name="parentId"/>
    /// e falha com 409 se já existir um item com esse nome.
    /// </summary>
    Task<RemoteItem> UploadAsync(string driveId, string parentId, string name, string? itemId, string? ifMatchETag,
        string localFile, DateTimeOffset modified, Action<long>? progress, CancellationToken ct);

    Task<RemoteItem> CreateFolderAsync(string driveId, string parentId, string name, CancellationToken ct);
    Task DeleteAsync(string driveId, string itemId, CancellationToken ct);
    Task<RemoteItem> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct);
    Task<RemoteItem> SetModifiedAsync(string driveId, string itemId, DateTimeOffset modified, CancellationToken ct);
}
