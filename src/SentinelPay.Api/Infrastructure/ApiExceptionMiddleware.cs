using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentinelPay.Application.Abstractions;
using SentinelPay.Application.Payments;
using SentinelPay.Application.Settlements;
using SentinelPay.Domain;

namespace SentinelPay.Api.Infrastructure;

public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ApiExceptionMiddleware(
        RequestDelegate next,
        ILogger<ApiExceptionMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            await WriteProblemAsync(context, exception);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception exception)
    {
        var concurrencyExtensions = exception is DbUpdateConcurrencyException concurrencyException &&
            _environment.IsEnvironment("Testing")
                ? await GetConcurrencyDiagnosticsAsync(concurrencyException, context.RequestAborted)
                : EmptyExtensions();
        var (status, type, title, detail, extensions) = exception switch
        {
            PaymentNotFoundException => (
                StatusCodes.Status404NotFound,
                "https://sentinelpay.dev/problems/payment-not-found",
                "Payment not found",
                exception.Message,
                EmptyExtensions()),
            IdempotencyConflictException => (
                StatusCodes.Status409Conflict,
                "https://sentinelpay.dev/problems/idempotency-conflict",
                "Idempotency conflict",
                exception.Message,
                EmptyExtensions()),
            UnsupportedProviderException => (
                StatusCodes.Status422UnprocessableEntity,
                "https://sentinelpay.dev/problems/unsupported-provider",
                "Unsupported payment provider",
                exception.Message,
                EmptyExtensions()),
            PaymentProviderException providerException => (
                StatusCodes.Status422UnprocessableEntity,
                "https://sentinelpay.dev/problems/provider-operation-failed",
                "Payment provider rejected the operation",
                providerException.Message,
                new Dictionary<string, object?> { ["providerCode"] = providerException.Code }),
            PaymentProviderUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "https://sentinelpay.dev/problems/provider-unavailable",
                "Payment provider unavailable",
                exception.Message,
                new Dictionary<string, object?> { ["retryable"] = true }),
            InvalidWebhookSignatureException => (
                StatusCodes.Status401Unauthorized,
                "https://sentinelpay.dev/problems/invalid-webhook-signature",
                "Invalid webhook signature",
                exception.Message,
                EmptyExtensions()),
            InvalidWebhookPayloadException => (
                StatusCodes.Status422UnprocessableEntity,
                "https://sentinelpay.dev/problems/invalid-webhook-payload",
                "Invalid webhook payload",
                exception.Message,
                EmptyExtensions()),
            DomainException => (
                StatusCodes.Status422UnprocessableEntity,
                "https://sentinelpay.dev/problems/domain-rule-violation",
                "Payment operation is not valid",
                exception.Message,
                EmptyExtensions()),
            NoPayableBalanceException => (
                StatusCodes.Status409Conflict,
                "https://sentinelpay.dev/problems/no-payable-balance",
                "No payable balance",
                exception.Message,
                EmptyExtensions()),
            UnauthorizedAccessException => (
                StatusCodes.Status401Unauthorized,
                "https://sentinelpay.dev/problems/unauthorized",
                "Unauthorized",
                "Valid merchant credentials are required.",
                EmptyExtensions()),
            KeyNotFoundException => (
                StatusCodes.Status404NotFound,
                "https://sentinelpay.dev/problems/resource-not-found",
                "Resource not found",
                exception.Message,
                EmptyExtensions()),
            ArgumentException => (
                StatusCodes.Status422UnprocessableEntity,
                "https://sentinelpay.dev/problems/invalid-request",
                "Invalid request",
                exception.Message,
                EmptyExtensions()),
            DistributedLockUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "https://sentinelpay.dev/problems/coordination-unavailable",
                "Coordination service unavailable",
                "The operation cannot be safely coordinated. Retry with the same idempotency key.",
                new Dictionary<string, object?> { ["retryable"] = true }),
            TimeoutException => (
                StatusCodes.Status503ServiceUnavailable,
                "https://sentinelpay.dev/problems/concurrency-timeout",
                "Payment resource is busy",
                "The operation could not acquire its concurrency lock. Retry with the same idempotency key.",
                EmptyExtensions()),
            DbUpdateConcurrencyException => (
                StatusCodes.Status409Conflict,
                "https://sentinelpay.dev/problems/concurrent-update",
                "Concurrent payment update",
                "The payment changed during this operation. Retry with the same idempotency key.",
                concurrencyExtensions),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (
                StatusCodes.Status409Conflict,
                "https://sentinelpay.dev/problems/duplicate-resource",
                "Duplicate payment operation",
                "A resource with the same unique operation key already exists.",
                EmptyExtensions()),
            _ => (
                StatusCodes.Status500InternalServerError,
                "https://sentinelpay.dev/problems/internal-error",
                "Unexpected server error",
                "An unexpected error occurred.",
                EmptyExtensions())
        };

        if (status >= 500)
        {
            _logger.LogError(exception, "Request {Method} {Path} failed with {StatusCode}.",
                context.Request.Method, context.Request.Path, status);
        }
        else
        {
            _logger.LogWarning(exception, "Request {Method} {Path} was rejected with {StatusCode}.",
                context.Request.Method, context.Request.Path, status);
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type,
            title,
            status,
            detail,
            instance = context.Request.Path.Value,
            traceId = context.TraceIdentifier,
            extensions
        });
    }

    private static async Task<Dictionary<string, object?>> GetConcurrencyDiagnosticsAsync(
        DbUpdateConcurrencyException exception,
        CancellationToken cancellationToken)
    {
        var entries = new List<object>();
        foreach (var entry in exception.Entries)
        {
            var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken);
            var primaryKey = entry.Metadata.FindPrimaryKey();
            entries.Add(new
            {
                entity = entry.Metadata.ClrType.Name,
                state = entry.State.ToString(),
                databaseExists = databaseValues is not null,
                key = primaryKey is null
                    ? null
                    : string.Join(',', primaryKey.Properties.Select(property =>
                        entry.CurrentValues[property]?.ToString()))
            });
        }

        return new Dictionary<string, object?>
        {
            ["entryCount"] = exception.Entries.Count,
            ["entries"] = entries
        };
    }

    private static Dictionary<string, object?> EmptyExtensions() => [];
}
