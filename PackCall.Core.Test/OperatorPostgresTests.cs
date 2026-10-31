using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Entities;
using PackCall.Core.Journaling;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using PackCall.Core.Services;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Sending;
using Microsoft.AspNetCore.Http;
using PackCall.Core.Diagnostics;

namespace PackCall.Core.Test;

public partial class CampaignPausePostgresTests
{
    private DeliveryProcessingService WithSender(IServiceProvider provider, IMessageSender sender, bool simulate = false) =>
        new(provider.GetRequiredService<IDeliveryRepository>(), provider.GetRequiredService<ICampaingRepository>(),
            sender, provider.GetRequiredService<IDeliveryStatusJournal>(), new SendPolicy(simulate));

    [PostgresTheory]
    [InlineData("422",false)] [InlineData("422",true)]
    [InlineData("415",false)] [InlineData("415",true)]
    [InlineData("500",false)] [InlineData("500",true)]
    [InlineData("malformed",false)] [InlineData("malformed",true)]
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
            c.Response.ContentType="application/json"; await c.Response.WriteAsync("{}");
        });
        if(failure=="refused") await server.DisposeAsync();
        using var sender=server.Sender(failure=="timeout" ? .1 : 5);
        await using var scope=_provider!.CreateAsyncScope();
        var service=WithSender(scope.ServiceProvider,sender,simulate);
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
        using var adapter=server.Sender();
        IMessageSender sender=http?adapter:new MockEmailSender(new FixedOperatorRandom(rejected?0:.5));
        await using var scope=_provider!.CreateAsyncScope();
        var service=WithSender(scope.ServiceProvider,sender,true);
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
        var actual=WithSender(scope.ServiceProvider,new MockEmailSender(new OperatorRandom()));
        await actual.ProcessAsync(next,default);
        var before=Snapshot(await ReadDelivery(old.Id)); var journal=await Journal(old.Id);
        release.TrySetResult(); await Task.Delay(100);
        Assert.Equal(before,Snapshot(await ReadDelivery(old.Id))); Assert.Equal(journal,await Journal(old.Id));
        Assert.Equal(DeliveryStatus.Sent,(await ReadDelivery(old.Id)).DeliveryStatus);
    }
}
