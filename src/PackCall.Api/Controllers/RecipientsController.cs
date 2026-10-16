using Microsoft.AspNetCore.Mvc;
using PackCall.Api.Models;
using PackCall.Core.Services;

namespace PackCall.Api.Controllers;

/// <summary>Чтение статичной аудитории.</summary>
[ApiController]
[Route("api/recipients")]
public sealed class RecipientsController(ICampaignService campaigns) : ControllerBase
{
    /// <summary>Возвращает страницу получателей.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PageResponse<RecipientResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PageResponse<RecipientResponse>>> GetRecipients(
        [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var page = await campaigns.GetRecipientsAsync(pageNumber, pageSize, ct);
        return Ok(page.ToResponse(pageNumber, pageSize, recipient => recipient.ToResponse()));
    }

    /// <summary>Возвращает получателя по идентификатору.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RecipientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecipientResponse>> GetRecipient(Guid id, CancellationToken ct)
    {
        var recipient = await campaigns.GetRecipientAsync(id, ct);
        return recipient is null ? NotFound() : Ok(recipient.ToResponse());
    }
}
