using PackCall.Core.Entities;

namespace PackCall.Core.Repositorys
{
    public interface ICampaingRepository
    {
        Task<Campaign?> Get(Guid id, CancellationToken ct);
        Task<CampaignDetails?> GetDetailsAsync(Guid id, CancellationToken ct);
        Task<IReadOnlyList<string>> GetChannelsAsync(Guid id, CancellationToken ct);
        /// <summary>
        /// Возвращает страницу в порядке Id и общее число кампаний; нумерация с 1.
        /// </summary>
        Task<PageResult<Campaign>> GetPage(int pageNumber, int pageSize, CancellationToken ct);
        Task<Campaign> Create(Campaign campaign, CancellationToken ct);
        /// <summary>Атомарно сохраняет кампанию и снимок. Возвращает false, если выбранный контакт отсутствует.</summary>
        Task<bool> CreateWithSnapshotAsync(Campaign campaign, Guid[]? contactIds,
            IReadOnlyList<string> channels, CancellationToken ct);
        Task<Campaign> Update(Campaign campaign, CancellationToken ct);
        /// <summary>Сохраняет только статус и даты, если статус в БД всё ещё равен ожидаемому.</summary>
        Task<bool> TryUpdateStatusAsync(Campaign campaign, CampaignStatus expectedStatus, CancellationToken ct);
        Task<bool> TryPublishDraftAsync(Campaign campaign, CancellationToken ct);
        Task<CampaignDeleteResult> DeleteIfInactiveAsync(Guid id, CancellationToken ct);
        Task DeleteIfFormingAsync(Guid id, CancellationToken ct);
    }

    public enum CampaignDeleteResult
    {
        NotFound,
        Deleted,
        Active
    }
}
