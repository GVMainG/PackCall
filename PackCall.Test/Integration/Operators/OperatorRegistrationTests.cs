using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Sending;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Journaling;
using PackCall.Core.Services;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;
using PackCall.Infrastructure.Sending;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Operators")]
public class OperatorRegistrationTests : OperatorTestBase
{
    [Theory]
    [InlineData("inprocess",false)] [InlineData("inprocess",true)]
    [InlineData("http",false)] [InlineData("http",true)]
    public async Task ProductionRegistrationMapsSimulateErrorsIntoProcessing(string mode, bool simulate)
    {
        await using var server=await OperatorServer.Start(async context => {
            using var body=await JsonDocument.ParseAsync(context.Request.Body);
            var flag=body.RootElement.GetProperty("simulate_errors").GetBoolean();
            Assert.Equal(simulate,flag);
            context.Response.ContentType="application/json";
            await context.Response.WriteAsync(flag ? "{\"result\":\"rejected\",\"code\":\"SimulatedOperatorRejected\"}" : "{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\"}");
        });
        var settings=TestOperatorSettings.Parse(Config(mode:mode,simulate:simulate.ToString())) with { OperatorUrl=server.Url };
        var deliveries=new FakeDeliveryRepository();var campaigns=new FakeCampaignRepository(deliveries);
        var campaign=Campaign.Create("Policy","text");campaign.StatusFromDraftToInProgress();
        await campaigns.Create(campaign,default);
        var delivery=Delivery.Create(campaign.Id,Guid.NewGuid(),"test@example.test",DateTime.UtcNow);deliveries.Add(delivery);
        var services=new ServiceCollection().AddDeliveryProcessing(settings);
        services.AddSingleton<IDeliveryRepository>(deliveries).AddSingleton<ICampaingRepository>(campaigns)
            .AddSingleton<IDeliveryStatusJournal>(new FakeJournal()).AddSingleton<IOperatorRandom>(new SequenceRandom(0));
        await using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
        var processing=scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var captured=Assert.Single(await processing.CaptureBatchAsync(1,"policy",TimeSpan.FromSeconds(90),default));
        await processing.ProcessAsync(captured,default);
        Assert.Equal(simulate?DeliveryStatus.Failed:DeliveryStatus.Sent,delivery.DeliveryStatus);
    }

}
