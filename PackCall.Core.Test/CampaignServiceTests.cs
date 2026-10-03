using PackCall.Core.Entities;
using PackCall.Core.Services;

namespace PackCall.Core.Test;

public class CampaignServiceTests
{
    private readonly FakeCampaignRepository _campaignRepository = new();
    private readonly FakeDeliveryRepository _deliveryRepository = new();
    private readonly CampaignService _service;

    public CampaignServiceTests() =>
        _service = new CampaignService(_campaignRepository, _deliveryRepository);

    private Task<Guid> CreateCampaignAsync() =>
        _service.CreateCampaignAsync("Кампания", "Текст", CancellationToken.None);

    [Fact]
    public async Task StartCampaign_TransitionsToInProgress()
    {
        var id = await CreateCampaignAsync();

        await _service.StartCampaignAsync(id, CancellationToken.None);

        var campaign = await _service.GetCampaignAsync(id, CancellationToken.None);
        Assert.Equal(CampaignStatus.InProgress, campaign!.Status);
    }

    [Fact]
    public async Task DeleteCampaign_InProgress_Throws()
    {
        var id = await CreateCampaignAsync();
        await _service.StartCampaignAsync(id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DeleteCampaignAsync(id, CancellationToken.None));
    }
}
