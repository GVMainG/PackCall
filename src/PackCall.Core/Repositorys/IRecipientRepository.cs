using PackCall.Core.Entities;

namespace PackCall.Core.Repositorys;

/// <summary>
/// Заранее заданная аудитория рассылок MVP.
/// </summary>
public interface IRecipientRepository
{
    Task<IReadOnlyList<Recipient>> GetAllAsync(CancellationToken ct);
    Task<Recipient?> GetAsync(Guid id, CancellationToken ct);
    Task<PageResult<Recipient>> GetPageAsync(int pageNumber, int pageSize, CancellationToken ct);
}
