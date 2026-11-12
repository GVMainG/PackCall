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
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Ресурс не найден"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Некорректный запрос"),
            RecipientAlreadyExistsException => (StatusCodes.Status409Conflict, "Получатель с таким email уже существует"),
            DomainException or InvalidOperationException =>
                (StatusCodes.Status409Conflict, "Операция недоступна в текущем состоянии"),
            _ => (0, null)
        };
        if (status == 0)
            return false;

        _logger.Warning(exception, "Ошибка запроса API: {StatusCode}", status);
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Instance = httpContext.Request.Path
        }, cancellationToken);
        return true;
    }
}
