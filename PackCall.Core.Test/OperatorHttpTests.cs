using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PackCall.Infrastructure.Hosting;
using PackCall.Infrastructure.Sending;

namespace PackCall.Core.Test;

public class OperatorHttpTests
{
    [Fact]
    public async Task SharedEmailFixturesMatchProductValidation()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"email-fixtures.json")));
        var sender = new MockEmailSender(new OperatorRandom());
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var value = item.GetProperty("value").GetString()!;
            var request = item.TryGetProperty("field", out var field) && field.GetString() == "message"
                ? TestOperatorTests.Request with { MessageText = value }
                : TestOperatorTests.Request with { RecipientEmail = value };
            if (item.GetProperty("valid").GetBoolean()) Assert.True((await sender.SendAsync(request, false, default)).IsSuccess);
            else await Assert.ThrowsAsync<TestOperatorValidationException>(() => sender.SendAsync(request, false, default));
        }
    }

    [Fact]
    public async Task ActualHttpPayloadContainsEveryBooleanAndParsesConfirmedResults()
    {
        var flags = new List<bool>();
        await using var server = await OperatorServer.Start(async context => {
            Assert.Equal("/v1/email/send", context.Request.Path);
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            flags.Add(body.RootElement.GetProperty("simulate_errors").GetBoolean());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(flags.Count % 2 == 0 ? "{\"result\":\"rejected\",\"code\":\"SimulatedOperatorRejected\"}" : "{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\"}");
        });
        using var sender = server.Sender();
        foreach (var flag in new[]{ false, false, true, true }) await sender.SendAsync(TestOperatorTests.Request, flag, default);
        Assert.Equal(new[]{false,false,true,true}, flags);
    }

    [Theory]
    [InlineData(422, "{}", "application/json", typeof(TestOperatorValidationException))]
    [InlineData(415, "{}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(500, "marker", "application/json", typeof(TestOperatorServiceException))]
    [InlineData(302, "", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "bad marker", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{\"result\":\"accepted\",\"code\":\"SimulatedOperatorRejected\"}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\",\"extra\":1}", "application/json", typeof(TestOperatorProtocolException))]
    [InlineData(200, "{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\"}", "text/plain", typeof(TestOperatorProtocolException))]
    public async Task ErrorsNeverBecomeConfirmedResults(int status, string body, string contentType, Type error)
    {
        await using var server = await OperatorServer.Start(async context => {
            context.Response.StatusCode = status; context.Response.ContentType = contentType;
            await context.Response.WriteAsync(body);
        });
        using var sender = server.Sender();
        foreach (var flag in new[]{false,true})
        {
            var ex = await Record.ExceptionAsync(() => sender.SendAsync(TestOperatorTests.Request, flag, default));
            Assert.IsType(error, ex); Assert.Null(ex!.InnerException); Assert.DoesNotContain("marker",ex.ToString());
        }
    }

    [Fact]
    public async Task ResponseLimitTimeoutAndExternalCancellation()
    {
        await using (var server = await OperatorServer.Start(c => c.Response.WriteAsync(new string('x',4097))))
        {
            using var sender = server.Sender();
            await Assert.ThrowsAsync<TestOperatorProtocolException>(() => sender.SendAsync(TestOperatorTests.Request,false,default));
        }
        await using (var server = await OperatorServer.Start(async c => {
            try { await Task.Delay(10000,c.RequestAborted); } catch(OperationCanceledException) { }
        }))
        {
            using var sender = server.Sender(.1);
            await Assert.ThrowsAsync<TestOperatorTimeoutException>(() => sender.SendAsync(TestOperatorTests.Request,false,default));
            using var ct = new CancellationTokenSource(20);
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(TestOperatorTests.Request,false,ct.Token));
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
            await Assert.ThrowsAsync<TestOperatorTransportException>(() => sender.SendAsync(TestOperatorTests.Request,false,default));
        }
        using var refused=new HttpTestEmailSender(new(TestSenderMode.Http,false,url));
        await Assert.ThrowsAsync<TestOperatorTransportException>(() => refused.SendAsync(TestOperatorTests.Request,false,default));
    }
}

internal sealed class OperatorServer(WebApplication app, Uri url) : IAsyncDisposable
{
    public Uri Url => url;
    public HttpTestEmailSender Sender(double seconds=5) => new(new(TestSenderMode.Http,false,url,TimeSpan.FromSeconds(seconds)));
    public static async Task<OperatorServer> Start(RequestDelegate handler)
    {
        var builder=WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app=builder.Build(); app.Run(handler); await app.StartAsync();
        var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app,new Uri(url));
    }
    private bool _disposed;
    public async ValueTask DisposeAsync() { if(_disposed)return; _disposed=true; await app.StopAsync(); await app.DisposeAsync(); }
}
