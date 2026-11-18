using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PackCall.Core.Entities;
using PackCall.Infrastructure.Hosting;
using PackCall.Infrastructure.Sending;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Operators")]
public class OperatorHttpTests
{
    [Fact]
    public async Task HttpRejectsTelegramBeforeCallingOperator()
    {
        var calls = 0;
        await using var server = await OperatorServer.Start(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        using var sender = server.Sender();
        var request = TestOperatorTests.Request with { Channel = ContactChannel.Telegram };
        await Assert.ThrowsAsync<TestOperatorValidationException>(() => sender.SendAsync(request, default));
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task SharedEmailFixturesMatchProductValidation()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"email-fixtures.json")));
        var sender = new MockEmailSender(new OperatorRandom(), simulateErrors: false);
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var value = item.GetProperty("value").GetString()!;
            var request = item.TryGetProperty("field", out var field) && field.GetString() == "message"
                ? TestOperatorTests.Request with { MessageText = value }
                : TestOperatorTests.Request with { Address = value };
            if (item.GetProperty("valid").GetBoolean()) Assert.True((await sender.SendAsync(request, default)).IsSuccess);
            else await Assert.ThrowsAsync<TestOperatorValidationException>(() => sender.SendAsync(request, default));
        }
    }

    [Fact]
    public async Task ActualHttpPayloadContainsEveryBooleanAndParsesConfirmedResults()
    {
        var flags = new List<bool>();
        var outcomes = new Queue<bool>([false, true, false, true]);
        var request = TestOperatorTests.Request;
        await using var server = await OperatorServer.Start(async context =>
        {
            Assert.Equal("POST", context.Request.Method);
            Assert.Equal("/v1/email/send", context.Request.Path);
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var payload = body.RootElement;
            Assert.Equal(6, payload.EnumerateObject().Count());
            Assert.Equal(request.DeliveryId, payload.GetProperty("delivery_id").GetGuid());
            Assert.Equal(request.CampaignId, payload.GetProperty("campaign_id").GetGuid());
            Assert.Equal(request.ContactId, payload.GetProperty("recipient_id").GetGuid());
            Assert.Equal(request.Address, payload.GetProperty("recipient_email").GetString());
            Assert.Equal(request.MessageText, payload.GetProperty("message_text").GetString());
            flags.Add(payload.GetProperty("simulate_errors").GetBoolean());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(outcomes.Dequeue()
                ? """{"result":"accepted","code":"AcceptedByProvider"}"""
                : """{"result":"rejected","code":"SimulatedOperatorRejected"}""");
        });

        using var disabled = server.Sender(simulateErrors: false);
        using var enabled = server.Sender(simulateErrors: true);
        foreach (var sender in new[] { disabled, enabled })
        {
            var rejection = await sender.SendAsync(request, default);
            Assert.False(rejection.IsSuccess);
            Assert.Equal("SimulatedOperatorRejected", rejection.Error);

            var acceptance = await sender.SendAsync(request, default);
            Assert.True(acceptance.IsSuccess);
            Assert.Null(acceptance.Error);
        }

        Assert.Equal(new[] { false, false, true, true }, flags);
        Assert.Empty(outcomes);
    }

    [Theory]
    [InlineData(422, "{}", "application/json", typeof(TestOperatorValidationException))]
    [InlineData(415, "{}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(500, "marker", "application/json", typeof(TestOperatorServiceException))]
    [InlineData(302, "", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "bad marker", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{\"result\":\"accepted\",\"code\":\"SimulatedOperatorRejected\"}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{\"result\":\"rejected\",\"code\":\"AcceptedByProvider\"}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\",\"extra\":1}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\"}", "text/plain", typeof(TestOperatorProtocolException))]
    public async Task ErrorsNeverBecomeConfirmedResults(int status, string body, string contentType, Type error)
    {
        await using var server = await OperatorServer.Start(async context => {
            context.Response.StatusCode = status; context.Response.ContentType = contentType;
            await context.Response.WriteAsync(body);
        });
        foreach (var flag in new[] { false, true })
        {
            using var sender = server.Sender(simulateErrors: flag);
            var ex = await Record.ExceptionAsync(() => sender.SendAsync(TestOperatorTests.Request, default));
            Assert.IsType(error, ex); Assert.Null(ex!.InnerException); Assert.DoesNotContain("marker",ex.ToString());
        }
    }

    [Fact]
    public async Task ResponseSizeLimitDistinguishesValidJsonFromOversizedJson()
    {
        const string accepted = "{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\"}";
        await using var server = await OperatorServer.Start(async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var length = body.RootElement.GetProperty("simulate_errors").GetBoolean() ? 4097 : 4096;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(accepted.PadRight(length));
        });
        using var exact = server.Sender(simulateErrors: false);
        using var oversized = server.Sender(simulateErrors: true);

        Assert.True((await exact.SendAsync(TestOperatorTests.Request, default)).IsSuccess);
        await Assert.ThrowsAsync<TestOperatorProtocolException>(() =>
            oversized.SendAsync(TestOperatorTests.Request, default));
    }

    [Fact]
    public async Task ResponseLimitTimeoutAndExternalCancellation()
    {
        await using (var server = await OperatorServer.Start(c => c.Response.WriteAsync(new string('x',4097))))
        {
            using var sender = server.Sender();
            await Assert.ThrowsAsync<TestOperatorProtocolException>(() => sender.SendAsync(TestOperatorTests.Request, default));
        }
        await using (var server = await OperatorServer.Start(async c => {
            try { await Task.Delay(10000,c.RequestAborted); } catch(OperationCanceledException) { }
        }))
        {
            using var sender = server.Sender(.1);
            await Assert.ThrowsAsync<TestOperatorTimeoutException>(() => sender.SendAsync(TestOperatorTests.Request, default));
            using var ct = new CancellationTokenSource(20);
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(TestOperatorTests.Request, ct.Token));
            Assert.Equal(ct.Token,exception.CancellationToken);
        }
    }

    [Fact]
    public async Task DisconnectAndConnectionRefusedAreTransportErrors()
    {
        Uri url;
        await using (var server = await OperatorServer.Start(c => { c.Abort(); return Task.CompletedTask; }))
        {
            url=server.Url;
            using var sender=server.Sender();
            await Assert.ThrowsAsync<TestOperatorTransportException>(() => sender.SendAsync(TestOperatorTests.Request, default));
        }
        using var refused=new HttpTestEmailSender(new(TestSenderMode.Http,false,url));
        await Assert.ThrowsAsync<TestOperatorTransportException>(() => refused.SendAsync(TestOperatorTests.Request, default));
    }
}
