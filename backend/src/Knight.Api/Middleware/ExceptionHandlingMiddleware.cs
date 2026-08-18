using Microsoft.AspNetCore.Mvc;
using Knight.Application.Exceptions;
using Knight.Contracts.Common;
using Knight.Domain.Exceptions;
using ValidationException = Knight.Application.Exceptions.ValidationException;

namespace Knight.Api.Middleware;

/// <summary>
/// Translates exceptions into RFC 7807 Problem Details responses. Never leaks
/// internal exception details (stack traces, connection strings, SQL text or database
/// constraint names, etc.) to clients — those are logged, not returned.
///
/// A <see cref="DomainException"/> is mapped by its <see cref="DomainErrorCategory"/>:
/// <c>Validation</c> becomes 400 (malformed input, invalid regardless of stored data)
/// and <c>Conflict</c> becomes 409 (a collision with existing state). The domain itself
/// never names a status code; the choice is made here.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            await HandleAsync(context, exception);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var (statusCode, title, errorCode) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "One or more validation errors occurred.", ApiErrorCodes.ValidationFailed),
            NotFoundException => (StatusCodes.Status404NotFound, exception.Message, ApiErrorCodes.NotFound),
            ForbiddenException => (StatusCodes.Status403Forbidden, exception.Message, ApiErrorCodes.Forbidden),
            ConflictException => (StatusCodes.Status409Conflict, exception.Message, ApiErrorCodes.Conflict),
            UniqueConstraintViolationException => (StatusCodes.Status409Conflict, "The request conflicts with existing data.", ApiErrorCodes.Conflict),
            DomainException { Category: DomainErrorCategory.Validation } =>
                (StatusCodes.Status400BadRequest, exception.Message, ApiErrorCodes.ValidationFailed),
            DomainException => (StatusCodes.Status409Conflict, exception.Message, ApiErrorCodes.Conflict),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", ApiErrorCodes.Unexpected)
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else
        {
            _logger.LogWarning(exception, "Handled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = $"https://httpstatuses.io/{statusCode}",
            Instance = context.Request.Path,
            Extensions =
            {
                ["errorCode"] = errorCode,
                ["correlationId"] = context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString()
            }
        };

        if (exception is ValidationException validationException)
        {
            problemDetails.Extensions["errors"] = validationException.Errors;
        }

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsJsonAsync(problemDetails);
    }
}
