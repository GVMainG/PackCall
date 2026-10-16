using PackCall.Core.Entities;

namespace PackCall.Core.Repositorys;

/// <summary>
/// Заранее заданная аудитория рассылок MVP.
/// </summary>
public interface IRecipientRepository
{
    Task<IReadOnlyList<Recipient>> GetAllAsync(CancellationToken ct);
}
