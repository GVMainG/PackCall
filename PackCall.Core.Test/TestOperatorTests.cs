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

public class TestOperatorTests
{
    public static SendRequest Request => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test+tag@example.test", "text");
    private static IConfiguration Config(string? environment = "test", string? mode = "inprocess", string? simulate = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["PACKCALL_ENVIRONMENT"] = environment, ["TEST_SENDER_MODE"] = mode,
            ["TEST_SIMULATE_ERRORS"] = simulate, ["TEST_OPERATOR_URL"] = "http://localhost:8000" }).Build();

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("unknown")]
    public void RequiredKeysNeverFallback(string? value)
    {
        Assert.Contains("PACKCALL_ENVIRONMENT", Assert.Throws<PackCallConfigurationException>(() => TestOperatorSettings.Parse(Config(value))).Message);
        Assert.Contains("TEST_SENDER_MODE", Assert.Throws<PackCallConfigurationException>(() => TestOperatorSettings.Parse(Config(mode: value))).Message);
    }

    [Fact]
    public void ProdGuardPrecedesModeAndStorage() => Assert.Equal("PACKCALL_ENVIRONMENT=prod is not supported",
        Assert.Throws<PackCallConfigurationException>(() => TestOperatorSettings.Parse(Config(" PROD ", null))).Message);

    [Theory]
    [InlineData(null, false)] [InlineData("FALSE", false)] [InlineData(" true ", true)]
    public void StrictBool(string? raw, bool expected) => Assert.Equal(expected, TestOperatorSettings.Parse(Config(simulate: raw)).SimulateErrors);

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("0")] [InlineData("1")] [InlineData("yes")]
    public void InvalidBool(string raw) => Assert.Throws<PackCallConfigurationException>(() => TestOperatorSettings.Parse(Config(simulate: raw)));

    [Theory]
    [InlineData("http://user:secret@localhost")] [InlineData("http://localhost/path")]
    [InlineData("http://localhost/?query=secret")] [InlineData("http://localhost/#secret")]
    [InlineData("ftp://localhost")] [InlineData("")]
    public void InvalidUrlsNeverLeakInput(string url)
    {
        var c=new ConfigurationBuilder().AddConfiguration(Config(mode:"http")).AddInMemoryCollection(new Dictionary<string,string?>{{"TEST_OPERATOR_URL",url}}).Build();
        Assert.Equal("TEST_OPERATOR_URL: invalid or missing configuration.",Assert.Throws<PackCallConfigurationException>(() => TestOperatorSettings.Parse(c)).Message);
    }

    [Theory]
    [InlineData("")] [InlineData("0")] [InlineData("-1")] [InlineData("NaN")] [InlineData("Infinity")] [InlineData("31")]
    public void InvalidTimeoutsFail(string value)
    {
        var c=new ConfigurationBuilder().AddConfiguration(Config(mode:"http")).AddInMemoryCollection(new Dictionary<string,string?>{{"TEST_OPERATOR_TIMEOUT_SECONDS",value}}).Build();
        Assert.Throws<PackCallConfigurationException>(() => TestOperatorSettings.Parse(c));
    }

    [Fact]
    public void LastConfigurationSourceWinsWithoutValidValueFallback()
    {
        var c=new ConfigurationBuilder().AddConfiguration(Config()).AddInMemoryCollection(new Dictionary<string,string?>{{"TEST_SENDER_MODE",""}}).Build();
        Assert.Throws<PackCallConfigurationException>(() => TestOperatorSettings.Parse(c));
        c=new ConfigurationBuilder().AddConfiguration(c).AddCommandLine(new[]{"--TEST_SENDER_MODE= HTTP "}).Build();
        Assert.Equal(TestSenderMode.Http,TestOperatorSettings.Parse(c).Mode);
    }

    [Fact]
    public async Task MockIndependentDrawPerCallAndExplicitFalse()
    {
        var random = new SequenceRandom(0, .009999, .01, .999999, 0, .5);
        var sender = new MockEmailSender(random);
        var request = Request;
        foreach (var expected in new[] { false, false, true, true, false, true })
            Assert.Equal(expected, (await sender.SendAsync(request, true, default)).IsSuccess);
        Assert.True((await sender.SendAsync(request, false, default)).IsSuccess);
        Assert.True((await sender.SendAsync(request, false, default)).IsSuccess);
        Assert.Equal(6, random.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InvalidRequestIsNeverConfirmedRejection(bool simulate)
    {
        var sender = new MockEmailSender(new SequenceRandom());
        await Assert.ThrowsAsync<TestOperatorValidationException>(() => sender.SendAsync(Request with { RecipientEmail = "payload\r\n@example.test" }, simulate, default));
    }

    [Theory]
    [InlineData("inprocess", typeof(MockEmailSender))] [InlineData("http", typeof(HttpTestEmailSender))]
    public void OneSender(string mode, Type expected)
    {
        var services = new ServiceCollection().AddDeliveryProcessing(TestOperatorSettings.Parse(Config(mode: mode)));
        using var provider = services.BuildServiceProvider();
        Assert.IsType(expected, Assert.Single(provider.GetServices<IMessageSender>()));
    }

    [Theory]
    [InlineData("0.000000001")] [InlineData("0.0005")] [InlineData("0.0011")]
    public void PositiveTimeoutRoundsUpToTimerResolution(string seconds)
    {
        var c=new ConfigurationBuilder().AddConfiguration(Config(mode:"http"))
            .AddInMemoryCollection(new Dictionary<string,string?>{{"TEST_OPERATOR_TIMEOUT_SECONDS",seconds}}).Build();
        var timeout=TestOperatorSettings.Parse(c).Timeout!.Value;
        var requested=double.Parse(seconds,System.Globalization.CultureInfo.InvariantCulture)*1000;
        Assert.Equal(Math.Max(1,Math.Ceiling(requested)),timeout.TotalMilliseconds);
    }

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

    private sealed class SequenceRandom(params double[] values) : IOperatorRandom
    {
        public int Calls;
        public double NextDouble() => values[Calls++];
    }
}
