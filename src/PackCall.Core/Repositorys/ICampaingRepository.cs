using PackCall.Core.Models;

namespace PackCall.Core.Repositorys
{
    public interface ICampaingRepository
    {
        Task<IEnumerable<Campaign>> Get(Guid id, CancellationToken ct);
        Task<IEnumerable<Campaign>> Get(Func<Campaign, bool> predicate, CancellationToken ct);
        Task<PageResult<Campaign>> GetPage(int pageNumber, int pageSize, CancellationToken ct);
        Task<Campaign> Create(Campaign campaign, CancellationToken ct);
        Task<Campaign> Update(Campaign campaign, CancellationToken ct);
        Task<bool> Delete(Guid id, CancellationToken ct);
    }
}
