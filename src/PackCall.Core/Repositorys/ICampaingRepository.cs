using PackCall.Core.Entities;

namespace PackCall.Core.Repositorys
{
    public interface ICampaingRepository
    {
        Task<IEnumerable<Campaign>> Get(Guid id, CancellationToken ct);
        /// <summary>
        /// Возвращает страницу в порядке Id и общее число кампаний; нумерация с 1.
        /// </summary>
        Task<PageResult<Campaign>> GetPage(int pageNumber, int pageSize, CancellationToken ct);
        Task<Campaign> Create(Campaign campaign, CancellationToken ct);
        Task<Campaign> Update(Campaign campaign, CancellationToken ct);
        /// <summary>Сохраняет только статус и даты, если статус в БД всё ещё равен ожидаемому.</summary>
        Task<bool> TryUpdateStatusAsync(Campaign campaign, CampaignStatus expectedStatus, CancellationToken ct);
        Task<bool> Delete(Guid id, CancellationToken ct);
    }
}
