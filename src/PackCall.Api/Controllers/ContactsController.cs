using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using PackCall.Api.Models;
using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Repositorys;

namespace PackCall.Api.Controllers;

/// <summary>Управление контактами и адресами.</summary>
[ApiController]
[Route("api/contacts")]
public sealed class ContactsController(IContactRepository contacts) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ContactResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<ActionResult<ContactResponse>> Create(CreateContactRequest request, CancellationToken ct)
    {
        if (request.DisplayName?.Contains('\0') == true)
            throw new ArgumentException("Имя контакта содержит недопустимый символ.");

        var contact = Contact.Create(Addresses(request.Channels), DateTime.UtcNow);
        contact.DisplayName = request.DisplayName;
        var created = await contacts.CreateAsync(contact, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created.ToContactResponse());
    }

    [HttpGet]
    [ProducesResponseType<PageResponse<ContactResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<PageResponse<ContactResponse>>> List(
        [FromQuery, Range(1, int.MaxValue)] int pageNumber = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20, CancellationToken ct = default)
    {
        var page = await contacts.GetContactsAsync(pageNumber, pageSize, ct);
        return Ok(page.ToResponse(pageNumber, pageSize, contact => contact.ToContactResponse()));
    }

    [HttpGet("{id}")]
    [ProducesResponseType<ContactResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<ContactResponse>> Get(Guid id, CancellationToken ct)
    {
        var contact = await contacts.GetContactAsync(id, ct);
        return contact is null ? NotFound() : Ok(contact.ToContactResponse());
    }

    [HttpPatch("{id}")]
    [ProducesResponseType<ContactResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<ActionResult<ContactResponse>> Replace(Guid id, ReplaceContactAddressesRequest request,
        CancellationToken ct)
    {
        var contact = await contacts.ReplaceAddressesAsync(id, Addresses(request.Channels), ct);
        return contact is null ? NotFound() : Ok(contact.ToContactResponse());
    }

    private static IEnumerable<(string Channel, string? Address)> Addresses(ContactChannelRequest?[] channels) =>
        channels.Select(channel => channel is null
            ? throw new ContactValidationException("Укажите канал и адрес для каждой записи.")
            : (channel.Channel, (string?)channel.Address)).ToArray();
}
