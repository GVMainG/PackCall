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
