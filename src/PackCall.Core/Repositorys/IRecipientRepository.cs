using PackCall.Core.Entities;

namespace PackCall.Core.Repositorys;

/// <summary>
/// Заранее заданная аудитория рассылок MVP.
/// </summary>
public interface IRecipientRepository
{
    /// <summary>Возвращает порцию уникальных получателей в порядке Id без подсчёта всей аудитории.</summary>
    Task<IReadOnlyList<Recipient>> GetBatchAsync(int offset, int batchSize, CancellationToken ct);
    Task<Recipient?> GetAsync(Guid id, CancellationToken ct);
    Task<PageResult<Recipient>> GetPageAsync(int pageNumber, int pageSize, CancellationToken ct);
}
