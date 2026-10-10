using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PackCall.Core.Exceptions;

namespace PackCall.Api.Middleware;

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            KeyNotFoundException => StatusCodes.Status404NotFound,
            ArgumentException => StatusCodes.Status400BadRequest,
            DomainException or InvalidOperationException => StatusCodes.Status409Conflict,
            _ => 0
        };
        if (status == 0)
            return false;

        logger.LogWarning(exception, "Ошибка запроса API: {StatusCode}", status);
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                400 => "Некорректный запрос",
                404 => "Ресурс не найден",
                _ => "Операция недоступна в текущем состоянии"
            },
            Instance = httpContext.Request.Path
        }, cancellationToken);
        return true;
    }
}
