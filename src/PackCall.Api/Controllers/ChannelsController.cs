using Microsoft.AspNetCore.Mvc;
using PackCall.Core.Entities;

namespace PackCall.Api.Controllers;

[ApiController]
[Route("api/channels")]
public sealed class ChannelsController : ControllerBase
{
    /// <summary>Возвращает каналы, поддерживаемые моделью PackCall.</summary>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> GetChannels() => Ok(ContactChannel.SupportedChannels);
}
