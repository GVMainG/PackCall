using PackCall.Core.Models;

namespace PackCall.Core.Services;

// Сценарии приложения: получение данных через порты Core,
// вызов методов Campaign и сохранение результата.
public sealed class CampaignService
{
    public Task<Guid> CreateCampaignAsync(string name, string messageText, CancellationToken ct)
    {
        // TODO: вызвать Campaign.Create и сохранить новую кампанию.
        throw new NotImplementedException();
    }

    public Task<Campaign> UpdateCampaignAsync(Guid campaignId, string name, string messageText, CancellationToken ct)
    {
        // TODO: загрузить кампанию, вызвать UpdateDetails и сохранить изменения.
        throw new NotImplementedException();
    }

    public Task<bool> DeleteCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        // TODO: загрузить кампанию, проверить допустимость удаления и удалить её.
        throw new NotImplementedException();
    }

    public Task StartCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        // TODO: загрузить кампанию, вызвать Start и сохранить изменения.
        throw new NotImplementedException();
    }

    public Task<Campaign?> GetCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        // TODO: получить кампанию через репозиторий.
        throw new NotImplementedException();
    }

    public Task<PageResult<Campaign>> GetCampaignsAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        // TODO: получить страницу кампаний через репозиторий.
        throw new NotImplementedException();
    }

    public Task<PageResult<DeliveryModel>> GetDeliveriesAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
    {
        // TODO: получить страницу доставок кампании через репозиторий.
        throw new NotImplementedException();
    }

    public Task<PageResult<RecipientModel>> GetRecipientsAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
    {
        // TODO: получить страницу получателей кампании через порт чтения.
        throw new NotImplementedException();
    }
}
