using PackCall.Core.Entities;

namespace PackCall.Core.Repositorys
{
    public interface ICampaingRepository
    {
        Task<IEnumerable<Campaign>> Get(Guid id, CancellationToken ct);
        Task<IEnumerable<Campaign>> Get(Func<Campaign, bool> predicate, CancellationToken ct);
        Task<PageResult<Campaign>> GetPage(int pageNumber, int pageSize, CancellationToken ct);
        /// <summary>
        /// Сохраняет новую кампанию вместе с её доставками атомарно:
        /// при ошибке ни кампания, ни её доставки не должны оставаться в хранилище.
        /// Получатели доставок уже существуют и не создаются повторно.
        /// </summary>
        Task<Campaign> Create(Campaign campaign, CancellationToken ct);
        Task<Campaign> Update(Campaign campaign, CancellationToken ct);
        Task<bool> Delete(Guid id, CancellationToken ct);
    }
}
