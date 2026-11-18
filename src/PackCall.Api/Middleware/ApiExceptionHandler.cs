using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PackCall.Core.Exceptions;

namespace PackCall.Api.Middleware;

// Преобразует ожидаемые ошибки операций в ProblemDetails, а остальные оставляет обработчику ASP.NET Core.
internal sealed class ApiExceptionHandler(Serilog.ILogger logger) : IExceptionHandler
{
    private readonly Serilog.ILogger _logger = logger.ForContext<ApiExceptionHandler>();

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            CampaignFormationException => (StatusCodes.Status500InternalServerError, "Не удалось сформировать кампанию"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Ресурс не найден"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Некорректный запрос"),
            ContactValidationException => (StatusCodes.Status400BadRequest, "Некорректные адреса контакта"),
            ContactAddressConflictException => (StatusCodes.Status409Conflict, "Адрес канала уже занят"),
            ContactWriteDeadlockException => (StatusCodes.Status503ServiceUnavailable, "Повторите запрос"),
            RecipientAlreadyExistsException => (StatusCodes.Status409Conflict, "Получатель с таким email уже существует"),
            ChannelOperatorUnavailableException => (StatusCodes.Status409Conflict, "Оператор канала недоступен"),
            DomainException or InvalidOperationException =>
                (StatusCodes.Status409Conflict, "Операция недоступна в текущем состоянии"),
            _ => (0, null)
        };
        if (status == 0)
            return false;

        _logger.Warning(exception, "Ошибка запроса API: {StatusCode}", status);
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception is ChannelOperatorUnavailableException ? exception.Message : null,
            Instance = httpContext.Request.Path
        }, options: null,
            contentType: exception is ChannelOperatorUnavailableException or CampaignFormationException ||
                httpContext.Request.Path.StartsWithSegments("/api/contacts", StringComparison.OrdinalIgnoreCase)
                ? "application/problem+json"
                : "application/json; charset=utf-8",
            cancellationToken);
        return true;
    }
}
