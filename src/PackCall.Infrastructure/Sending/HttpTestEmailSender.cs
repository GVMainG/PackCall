using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PackCall.Core.Sending;
using PackCall.Infrastructure.Hosting;

namespace PackCall.Infrastructure.Sending;

// HttpMessageInvoker over SocketsHttpHandler avoids HttpClientFactory logging and
// HttpClient's diagnostic wrapper; no request URL or payload is emitted by this adapter.
public sealed class HttpTestEmailSender : IMessageSender, IDisposable
{
    private readonly HttpMessageInvoker _client;
    private readonly Uri _endpoint;
    private readonly TimeSpan _timeout;

    public HttpTestEmailSender(TestOperatorSettings settings)
    {
        _endpoint = new Uri(settings.OperatorUrl!, "v1/email/send");
        _timeout = settings.Timeout ?? TimeSpan.FromSeconds(5);
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            ActivityHeadersPropagator = null
        });
    }

    public async Task<SendResult> SendAsync(SendRequest request, bool simulateErrors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        MockEmailSender.Validate(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(new
                {
                    delivery_id = request.DeliveryId, campaign_id = request.CampaignId,
                    recipient_id = request.RecipientId, recipient_email = request.RecipientEmail,
                    message_text = request.MessageText, simulate_errors = simulateErrors
                })
            };
            using var response = await _client.SendAsync(message, deadline.Token);
            ct.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity) throw new TestOperatorValidationException();
            if ((int)response.StatusCode >= 500) throw new TestOperatorServiceException();
            if (response.StatusCode != HttpStatusCode.OK
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new TestOperatorProtocolException();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[4097];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token);
                if (read == 0) break;
                count += read;
            }
            if (count > 4096) throw new TestOperatorProtocolException();
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
                throw new TestOperatorProtocolException();
            var outcome = (result.GetString(), code.GetString()) switch
            {
                ("accepted", "AcceptedByProvider") => SendResult.Success(),
                ("rejected", "SimulatedOperatorRejected") => SendResult.Failure("SimulatedOperatorRejected"),
                _ => throw new TestOperatorProtocolException()
            };
            ct.ThrowIfCancellationRequested();
            return outcome;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new TestOperatorTimeoutException();
        }
        catch (JsonException) { throw new TestOperatorProtocolException(); }
        catch (HttpRequestException) { throw new TestOperatorTransportException(); }
        catch (IOException) { throw new TestOperatorTransportException(); }
    }

    public void Dispose() => _client.Dispose();
}
