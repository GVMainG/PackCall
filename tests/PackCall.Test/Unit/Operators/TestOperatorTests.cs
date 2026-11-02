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

[Trait("Category", "Unit")]
[Trait("Module", "Operators")]
public class TestOperatorTests : OperatorTestBase
{
    public static SendRequest Request => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test+tag@example.test", "text");
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

}
