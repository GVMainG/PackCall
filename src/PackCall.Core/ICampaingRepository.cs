using PackCall.Core.Models;

namespace PackCall.Core
{
    public interface ICampaingRepository
    {
        Task<IEnumerable<CampaignModel>> Get(Guid id);
        Task<IEnumerable<CampaignModel>> Get(Func<CampaignModel, bool> predicate);
        Task<PageResult<CampaignModel>> GetPage(int pageNumber, int pageSize);
        Task<CampaignModel> Create(CampaignModel campaign);
        Task<CampaignModel> Update(CampaignModel campaign);
        Task<bool> Delete(Guid id);
    }
}