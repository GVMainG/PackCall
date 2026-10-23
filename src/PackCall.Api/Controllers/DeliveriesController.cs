using Microsoft.AspNetCore.Mvc;
using PackCall.Api.Models;
using PackCall.Core.Services;

namespace PackCall.Api.Controllers;

/// <summary>Чтение отдельных доставок.</summary>
[ApiController]
[Route("api/deliveries")]
public sealed class DeliveriesController(ICampaignService campaigns) : ControllerBase
{
    /// <summary>Возвращает доставку по идентификатору.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DeliveryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeliveryResponse>> GetDelivery(Guid id, CancellationToken ct)
    {
        var delivery = await campaigns.GetDeliveryAsync(id, ct);
        return delivery is null ? NotFound() : Ok(delivery.ToResponse());
    }
}
