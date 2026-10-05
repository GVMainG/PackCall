using PackCall.Core.Entities;
using PackCall.Core.Repositorys;

namespace PackCall.Core.Test;

public class RepositoryContractsTests
{
    [Fact]
    public async Task CampaignQuery_UsesExpressionToFilterStoredCampaigns()
    {
        ICampaingRepository repository = new FakeCampaignRepository();
        var draft = Campaign.Create("Черновик", "Текст");
        var started = Campaign.Create("Запущена", "Текст");
        started.StatusFromDraftToInProgress();
        await repository.Create(draft, CancellationToken.None);
        await repository.Create(started, CancellationToken.None);

        var result = await repository.Get(campaign => campaign.Status == CampaignStatus.InProgress, CancellationToken.None);

        Assert.Same(started, Assert.Single(result));
    }

    [Fact]
    public async Task DeliveryQuery_AppliesCampaignScopeAndStatusExpression()
    {
        var repository = new FakeDeliveryRepository();
        var campaignId = Guid.NewGuid();
        var waiting = Delivery.Create(campaignId, Guid.NewGuid(), "waiting@example.com", DateTime.UtcNow);
        var active = Delivery.Create(campaignId, Guid.NewGuid(), "active@example.com", DateTime.UtcNow);
        active.StatusFromWaitingToInProgress("worker-1", DateTime.UtcNow.AddMinutes(2));
        repository.Add(waiting);
        repository.Add(active);
        repository.Add(Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "other@example.com", DateTime.UtcNow));
        IDeliveryRepository contract = repository;

        var result = await contract.Get(campaignId,
            delivery => delivery.DeliveryStatus == DeliveryStatus.Waiting, CancellationToken.None);

        Assert.Same(waiting, Assert.Single(result));
    }
}
