using PackCall.Core.Models;

namespace PackCall.Core.Repositorys
{
    public interface ICampaingRepository
    {
        Task<IEnumerable<Campaign>> Get(Guid id);
        Task<IEnumerable<Campaign>> Get(Func<Campaign, bool> predicate);
        Task<PageResult<Campaign>> GetPage(int pageNumber, int pageSize);
        Task<Campaign> Create(Campaign campaign);
        Task<Campaign> Update(Campaign campaign);
        Task<bool> Delete(Guid id);
    }
}
