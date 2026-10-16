using System.Text;
using Microsoft.AspNetCore.Mvc;
using PackCall.Api.Metrics;
using PackCall.Core.Metrics;
using PackCall.Infrastructure.Metrics;

namespace PackCall.Api.Controllers;

/// <summary>Метрики обработки доставок для сбора Prometheus.</summary>
[ApiController]
[Route("metrics")]
public sealed class MetricsController(
    DeliveryMetrics metrics,
    IDeliveryStateSnapshotReader stateReader,
    ILogger<MetricsController> logger) : ControllerBase
{
    /// <summary>Возвращает накопительные счётчики процесса и текущее состояние доставок.</summary>
    [HttpGet]
    [Produces("text/plain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ContentResult> GetMetrics(CancellationToken ct)
    {
        DeliveryStateSnapshot? state;
        try
        {
            state = await stateReader.ReadAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Не удалось прочитать текущее состояние доставок для метрик");
            state = null;
        }

        return Content(PrometheusMetricsFormatter.Format(metrics.GetTotals(), state),
            "text/plain; version=0.0.4; charset=utf-8", Encoding.UTF8);
    }
}
