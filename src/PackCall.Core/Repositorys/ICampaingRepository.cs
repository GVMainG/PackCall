using PackCall.Core.Entities;
using System.Linq.Expressions;

namespace PackCall.Core.Repositorys
{
    public interface ICampaingRepository
    {
        Task<IEnumerable<Campaign>> Get(Guid id, CancellationToken ct);
        /// <summary>Фильтрует кампании в хранилище; выражение должно допускать перевод в SQL.</summary>
        Task<IEnumerable<Campaign>> Get(Expression<Func<Campaign, bool>> predicate, CancellationToken ct);
        /// <summary>Возвращает страницу в порядке Id и общее число кампаний; нумерация с 1.</summary>
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
