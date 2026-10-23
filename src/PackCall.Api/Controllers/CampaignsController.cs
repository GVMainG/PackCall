using Microsoft.AspNetCore.Mvc;
using PackCall.Api.Models;
using PackCall.Core.Services;

namespace PackCall.Api.Controllers;

/// <summary>Создание, запуск, приостановка, возобновление и чтение кампаний.</summary>
[ApiController]
[Route("api/campaigns")]
public sealed class CampaignsController(ICampaignService campaigns) : ControllerBase
{
    /// <summary>Возвращает страницу кампаний.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PageResponse<CampaignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PageResponse<CampaignResponse>>> GetCampaigns(
        [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var page = await campaigns.GetCampaignsAsync(pageNumber, pageSize, ct);
        return Ok(page.ToResponse(pageNumber, pageSize, campaign => campaign.ToResponse()));
    }

    /// <summary>Возвращает кампанию и число доставок по статусам.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CampaignDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CampaignDetailsResponse>> GetCampaign(Guid id, CancellationToken ct)
    {
        var campaign = await campaigns.GetCampaignAsync(id, ct);
        if (campaign is null)
            return NotFound();

        var counts = await campaigns.GetDeliveryStatusCountsAsync(id, ct);
        return Ok(new CampaignDetailsResponse(campaign.ToResponse(), counts.ToResponse()));
    }

    /// <summary>Создаёт черновик кампании и доставки для статичной аудитории.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CampaignResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CampaignResponse>> CreateCampaign(
        CreateCampaignRequest request, CancellationToken ct)
    {
        var id = await campaigns.CreateCampaignAsync(request.Name ?? "", request.MessageText ?? "", ct);
        var campaign = await campaigns.GetCampaignAsync(id, ct)
            ?? throw new InvalidOperationException("Созданная кампания не найдена.");
        return CreatedAtAction(nameof(GetCampaign), new { id }, campaign.ToResponse());
    }

    /// <summary>Изменяет поля черновика кампании.</summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(CampaignResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CampaignResponse>> UpdateCampaign(
        Guid id, UpdateCampaignRequest request, CancellationToken ct)
    {
        var existing = await campaigns.GetCampaignAsync(id, ct);
        if (existing is null)
            return NotFound();
        if (request.Name is null && request.MessageText is null)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Укажите хотя бы одно поле." });

        var updated = await campaigns.UpdateCampaignAsync(
            id, request.Name ?? existing.Name, request.MessageText ?? existing.MessageText, ct);
        return Ok(updated.ToResponse());
    }

    /// <summary>Запускает черновик кампании; повторный запуск уже запущенной кампании безопасен.</summary>
    [HttpPost("{id:guid}/start")]
    [ProducesResponseType(typeof(CampaignResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CampaignResponse>> StartCampaign(Guid id, CancellationToken ct)
    {
        await campaigns.StartCampaignAsync(id, ct);
        var campaign = await campaigns.GetCampaignAsync(id, ct)
            ?? throw new KeyNotFoundException("Кампания не найдена.");
        return Ok(campaign.ToResponse());
    }

    /// <summary>Приостанавливает запущенную кампанию; начатые отправки могут завершиться. Повторный запрос безопасен.</summary>
    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(typeof(CampaignResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CampaignResponse>> PauseCampaign(Guid id, CancellationToken ct)
    {
        await campaigns.PauseCampaignAsync(id, ct);
        var campaign = await campaigns.GetCampaignAsync(id, ct)
            ?? throw new KeyNotFoundException("Кампания не найдена.");
        return Ok(campaign.ToResponse());
    }

    /// <summary>Возобновляет приостановленную кампанию. Повторный запрос для запущенной кампании безопасен.</summary>
    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(typeof(CampaignResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CampaignResponse>> ResumeCampaign(Guid id, CancellationToken ct)
    {
        await campaigns.ResumeCampaignAsync(id, ct);
        var campaign = await campaigns.GetCampaignAsync(id, ct)
            ?? throw new KeyNotFoundException("Кампания не найдена.");
        return Ok(campaign.ToResponse());
    }

    /// <summary>Удаляет кампанию, если она не обрабатывается и не приостановлена.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteCampaign(Guid id, CancellationToken ct) =>
        await campaigns.DeleteCampaignAsync(id, ct) ? NoContent() : NotFound();

    /// <summary>Возвращает страницу доставок кампании.</summary>
    [HttpGet("{id:guid}/deliveries")]
    [ProducesResponseType(typeof(PageResponse<DeliveryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PageResponse<DeliveryResponse>>> GetDeliveries(
        Guid id, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (await campaigns.GetCampaignAsync(id, ct) is null)
            return NotFound();

        var page = await campaigns.GetDeliveriesAsync(id, pageNumber, pageSize, ct);
        return Ok(page.ToResponse(pageNumber, pageSize, delivery => delivery.ToResponse()));
    }
}
