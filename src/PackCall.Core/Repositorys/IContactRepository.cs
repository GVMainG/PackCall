using PackCall.Core.Entities;

namespace PackCall.Core.Repositorys;

/// <summary>
/// Заранее заданная аудитория рассылок MVP.
/// </summary>
public interface IContactRepository
{
    /// <summary>Сохраняет нового получателя. Повторный email отклоняется.</summary>
    Task<Contact> CreateAsync(Contact recipient, CancellationToken ct);
    /// <summary>Возвращает получателя по email без учёта регистра.</summary>
    Task<Contact?> GetByEmailAsync(string email, CancellationToken ct);
    /// <summary>Возвращает порцию уникальных получателей в порядке Id без подсчёта всей аудитории.</summary>
    Task<IReadOnlyList<Contact>> GetBatchAsync(int offset, int batchSize, CancellationToken ct);
    Task<Contact?> GetAsync(Guid id, CancellationToken ct);
    Task<PageResult<Contact>> GetPageAsync(int pageNumber, int pageSize, CancellationToken ct);
}
