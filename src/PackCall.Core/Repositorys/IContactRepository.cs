using PackCall.Core.Entities;

namespace PackCall.Core.Repositorys;

/// <summary>
/// Заранее заданная аудитория рассылок MVP.
/// </summary>
public interface IContactRepository
{
    Task<Contact?> GetContactAsync(Guid id, CancellationToken ct);
    Task<PageResult<Contact>> GetContactsAsync(int pageNumber, int pageSize, CancellationToken ct);
    /// <summary>Сохраняет контакт. Адрес, занятый в том же канале, отклоняется.</summary>
    Task<Contact> CreateAsync(Contact contact, CancellationToken ct);
    /// <summary>Атомарно заменяет адреса. Возвращает null, если контакт не найден.</summary>
    Task<Contact?> ReplaceAddressesAsync(Guid id, IEnumerable<(string Channel, string? Address)> addresses,
        CancellationToken ct);
    /// <summary>Возвращает получателя по email без учёта регистра.</summary>
    Task<Contact?> GetByEmailAsync(string email, CancellationToken ct);
    /// <summary>Возвращает порцию уникальных получателей в порядке Id без подсчёта всей аудитории.</summary>
    Task<IReadOnlyList<Contact>> GetBatchAsync(int offset, int batchSize, CancellationToken ct);
    Task<Contact?> GetAsync(Guid id, CancellationToken ct);
    Task<PageResult<Contact>> GetPageAsync(int pageNumber, int pageSize, CancellationToken ct);
}
