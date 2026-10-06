using PackCall.Core.Models;

namespace PackCall.Core.Repositorys
{
    public interface IDeliveryRepository
    {
        Task<IEnumerable<DeliveryModel>> Get(Guid id, CancellationToken ct);
        Task<IEnumerable<DeliveryModel>> Get(Guid compaingId, Func<DeliveryModel, bool> predicate, CancellationToken ct);
        Task<PageResult<DeliveryModel>> GetPage(Guid compaingId, int pageNumber, int pageSize, CancellationToken ct);

    }
}
