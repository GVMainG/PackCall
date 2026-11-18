using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Journaling;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using PackCall.Core.Services;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Sending;
using Microsoft.AspNetCore.Http;
using PackCall.Core.Diagnostics;

namespace PackCall.Core.Test;

[Collection("Measurement")]
[Trait("Category", "Integration")]
[Trait("Module", "Operators")]
public class OperatorPostgresTests : PostgresTestBase
{
    private DeliveryProcessingService WithSender(IServiceProvider provider, IMessageSender sender) =>
        new(provider.GetRequiredService<IDeliveryRepository>(), provider.GetRequiredService<ICampaingRepository>(),
            channel => channel == ContactChannel.Email ? sender : null, provider.GetRequiredService<IDeliveryStatusJournal>());

    [PostgresFact]
    public async Task RoutesPersistedAddressesToKeyedOperatorsAndLeavesUnregisteredChannelRecoverable()
    {
        var email = new FakeMessageSender();
        var telegram = new FakeMessageSender();
        var services = new ServiceCollection();
        AddServices(services);
        services.RemoveAllKeyed<IMessageSender>(ContactChannel.Email);
        services.AddKeyedSingleton<IMessageSender>(ContactChannel.Email, email);
        services.AddKeyedSingleton<IMessageSender>(ContactChannel.Telegram, telegram);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var contacts = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await contacts.CreateAsync(Contact.Create(
            [(ContactChannel.Email, "saved@example.test"), (ContactChannel.Telegram, "42")], DateTime.UtcNow), default);
        var campaign = await campaigns.CreateCampaignAsync("Routing", "Message", [contact.Id],
            [ContactChannel.Email, ContactChannel.Telegram], default);
        Assert.NotNull(await contacts.ReplaceAddressesAsync(contact.Id,
            [(ContactChannel.Email, "edited@example.test"), (ContactChannel.Telegram, "99")], default));
        await campaigns.StartCampaignAsync(campaign.Id, default);

        await using var emailOnlyScope = _provider!.CreateAsyncScope();
        var emailOnly = emailOnlyScope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var lease = TimeSpan.FromMinutes(2);
        var emailDelivery = Assert.Single(await emailOnly.CaptureBatchAsync(10, "email-only", lease, default));
        Assert.Equal(ContactChannel.Email, emailDelivery.Channel);
        Assert.Empty(await emailOnly.CaptureBatchAsync(10, "email-only", lease, default));

        var processor = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var telegramDelivery = Assert.Single(await processor.CaptureBatchAsync(10, "both-channels", lease, default));
        Assert.Equal(ContactChannel.Telegram, telegramDelivery.Channel);
        var before = Snapshot(await ReadDelivery(telegramDelivery.Id));
        var journal = await Journal(telegramDelivery.Id);
        await Assert.ThrowsAsync<ChannelOperatorUnavailableException>(() => emailOnly.ProcessAsync(telegramDelivery, default));
        Assert.Empty(_sender.Requests);
        Assert.Equal(before, Snapshot(await ReadDelivery(telegramDelivery.Id)));
        Assert.Equal(journal, await Journal(telegramDelivery.Id));

        Guid[] deliveryIds = [emailDelivery.Id, telegramDelivery.Id];
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expired = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE deliveries SET lock_expires_at = clock_timestamp() - interval '1 second'
            WHERE id = ANY ({deliveryIds})
            """);
        Assert.Equal(2, expired);
        Assert.Equal(2, await processor.ReleaseExpiredLocksAsync(default));
        foreach (var id in deliveryIds)
            Assert.Equal(DeliveryStatus.Waiting, (await ReadDelivery(id)).DeliveryStatus);

        var recovered = await processor.CaptureBatchAsync(10, "recovery", lease, default);
        Assert.Equal(deliveryIds.Order(), recovered.Select(delivery => delivery.Id));
        foreach (var delivery in recovered)
            await processor.ProcessAsync(delivery, default);
        Assert.Equal(new SendRequest(emailDelivery.Id, campaign.Id, contact.Id, ContactChannel.Email,
            "saved@example.test", "Message"), Assert.Single(email.Requests));
        Assert.Equal(new SendRequest(telegramDelivery.Id, campaign.Id, contact.Id, ContactChannel.Telegram,
            "42", "Message"), Assert.Single(telegram.Requests));
        Assert.Equal(DeliveryStatus.Sent, (await ReadDelivery(emailDelivery.Id)).DeliveryStatus);
        Assert.Equal(DeliveryStatus.Sent, (await ReadDelivery(telegramDelivery.Id)).DeliveryStatus);
    }

    [PostgresFact]
    public async Task CaptureSkipsTelegramDeliveryWithUnicodeWhitespaceAddress()
    {
        var services = new ServiceCollection();
        AddServices(services);
        services.AddKeyedSingleton<IMessageSender>(ContactChannel.Telegram, new FakeMessageSender());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var contacts = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await contacts.CreateAsync(
            Contact.Create([(ContactChannel.Telegram, "42")], DateTime.UtcNow), default);
        var campaign = await campaigns.CreateCampaignAsync(
            "Whitespace address", "Message", [contact.Id], [ContactChannel.Telegram], default);
        await campaigns.StartCampaignAsync(campaign.Id, default);

        const string whitespace = "\t\u00A0\u202F";
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deliveryId = await db.Deliveries.Where(delivery => delivery.CampaignId == campaign.Id)
            .Select(delivery => delivery.Id).SingleAsync();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE deliveries SET address_snapshot = {whitespace} WHERE id = {deliveryId}
            """));

        var processor = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        Assert.Empty(await processor.CaptureBatchAsync(10, "worker", TimeSpan.FromMinutes(2), default));
        var stored = await ReadDelivery(deliveryId);
        Assert.Equal(ContactChannel.Telegram, stored.Channel);
        Assert.Equal(whitespace, stored.AddressSnapshot);
        Assert.Equal(DeliveryStatus.Waiting, stored.DeliveryStatus);
        Assert.Null(stored.WorkerId);
        Assert.Null(stored.LockExpiresAt);
    }

    [PostgresTheory]
    [InlineData("422",false)] [InlineData("422",true)]
    [InlineData("415",false)] [InlineData("415",true)]
    [InlineData("500",false)] [InlineData("500",true)]
    [InlineData("malformed",false)] [InlineData("malformed",true)]
    [InlineData("accepted_wrong_code", false)] [InlineData("accepted_wrong_code", true)]
    [InlineData("rejected_wrong_code", false)] [InlineData("rejected_wrong_code", true)]
    [InlineData("disconnect",false)] [InlineData("disconnect",true)]
    [InlineData("refused",false)] [InlineData("refused",true)]
    [InlineData("timeout",false)] [InlineData("timeout",true)]
    public async Task OperatorErrorsPreserveLeaseWithoutTerminalJournal(string failure, bool simulate)
    {
        using var measurements = new MeasurementCollector(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = "unused" });
        await CreateStartedCampaignAsync();
        await using var server = await OperatorServer.Start(async c => {
            if(failure=="disconnect") { c.Abort(); return; }
            if(failure=="timeout") { try { await Task.Delay(10000,c.RequestAborted); } catch(OperationCanceledException){} return; }
            c.Response.StatusCode=int.TryParse(failure,out var status)?status:200;
            c.Response.ContentType = "application/json";
            await c.Response.WriteAsync(failure switch
            {
                "accepted_wrong_code" => """{"result":"accepted","code":"SimulatedOperatorRejected"}""",
                "rejected_wrong_code" => """{"result":"rejected","code":"AcceptedByProvider"}""",
                _ => "{}"
            });
        });
        if(failure=="refused") await server.DisposeAsync();
        using var sender=server.Sender(failure=="timeout" ? .1 : 5, simulate);
        await using var scope=_provider!.CreateAsyncScope();
        var service=WithSender(scope.ServiceProvider,sender);
        var delivery=Assert.Single(await service.CaptureBatchAsync(1,"worker-A",TimeSpan.FromSeconds(90),default));
        var before=Snapshot(await ReadDelivery(delivery.Id)); var journal=await Journal(delivery.Id);
        var error=await Record.ExceptionAsync(() => service.ProcessAsync(delivery,default));
        var expected=failure switch { "422"=>typeof(TestOperatorValidationException), "500"=>typeof(TestOperatorServiceException),
            "timeout"=>typeof(TestOperatorTimeoutException), "disconnect" or "refused"=>typeof(TestOperatorTransportException), _=>typeof(TestOperatorProtocolException) };
        Assert.IsType(expected,error);
        Assert.Equal(before,Snapshot(await ReadDelivery(delivery.Id))); Assert.Equal(journal,await Journal(delivery.Id));
        Assert.False(await service.CompleteCampaignIfFinishedAsync(delivery.CampaignId,default));
        var window = measurements.Close()!;
        var category = failure switch { "422" => Outcome.Validation, "500" => Outcome.Service,
            "timeout" => Outcome.Timeout, "disconnect" or "refused" => Outcome.Transport, _ => Outcome.Protocol };
        Assert.Equal(1, window.Counters[new((int)Event.OperatorFinished, category, Purpose.None)].Delta);
        Assert.All(window.Counters.Where(pair => pair.Key.Metric == (int)Event.TerminalApplied), pair => Assert.Equal(0, pair.Value.Delta));
    }

    [PostgresTheory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task ConfirmedOperatorResultsAreTerminal(bool http, bool rejected)
    {
        await CreateStartedCampaignAsync();
        await using var server=await OperatorServer.Start(async c => {
            c.Response.ContentType="application/json";
            await c.Response.WriteAsync(rejected?"{\"result\":\"rejected\",\"code\":\"SimulatedOperatorRejected\"}":"{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\"}");
        });
        using var adapter = server.Sender(simulateErrors: true);
        IMessageSender sender=http?adapter:new MockEmailSender(new FixedOperatorRandom(rejected?0:.5), simulateErrors: true);
        await using var scope=_provider!.CreateAsyncScope();
        var service=WithSender(scope.ServiceProvider,sender);
        var delivery=Assert.Single(await service.CaptureBatchAsync(1,"operator",TimeSpan.FromSeconds(90),default));
        await service.ProcessAsync(delivery,default);
        Assert.Equal(rejected?DeliveryStatus.Failed:DeliveryStatus.Sent,(await ReadDelivery(delivery.Id)).DeliveryStatus);
        Assert.Contains(rejected?"SimulatedOperatorRejected":"AcceptedByProvider",await Journal(delivery.Id));
        Assert.Empty(await service.CaptureBatchAsync(1,"other",TimeSpan.FromSeconds(90),default));
    }
    private sealed class FixedOperatorRandom(double value):IOperatorRandom { public double NextDouble()=>value; }

    [PostgresFact]
    public async Task LateHttpResponseAfterLeaseLossAndCancellationCannotOverwriteNewOwner()
    {
        await CreateStartedCampaignAsync();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server=await OperatorServer.Start(async c => {
            entered.TrySetResult(); await release.Task;
            c.Response.ContentType="application/json";
            await c.Response.WriteAsync("{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\"}");
        });
        using var sender=server.Sender(30);
        await using var scope=_provider!.CreateAsyncScope();
        var service=WithSender(scope.ServiceProvider,sender);
        var old=Assert.Single(await service.CaptureBatchAsync(1,"old",TimeSpan.FromMilliseconds(500),default));
        using var ct=new CancellationTokenSource();
        var processing=service.ProcessAsync(old,ct.Token);
        await entered.Task.WaitAsync(Timeout);
        await Task.Delay(600); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.Equal(1,await service.ReleaseExpiredLocksAsync(default));
        var next=Assert.Single(await service.CaptureBatchAsync(1,"new",TimeSpan.FromSeconds(90),default));
        var actual=WithSender(scope.ServiceProvider,new MockEmailSender(new OperatorRandom(), simulateErrors: false));
        await actual.ProcessAsync(next,default);
        var before=Snapshot(await ReadDelivery(old.Id)); var journal=await Journal(old.Id);
        release.TrySetResult(); await Task.Delay(100);
        Assert.Equal(before,Snapshot(await ReadDelivery(old.Id))); Assert.Equal(journal,await Journal(old.Id));
        Assert.Equal(DeliveryStatus.Sent,(await ReadDelivery(old.Id)).DeliveryStatus);
    }
}
