using SentinelPay.Api.Security;
using SentinelPay.Application.Abstractions;
using SentinelPay.Application.Reconciliation;
using System.Text;

namespace SentinelPay.Api.Endpoints;

public static class ReconciliationEndpoints
{
    private const long MaximumReportBytes = 2 * 1024 * 1024;

    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/reconciliation/imports/{provider}", ImportAsync)
            .WithTags("Reconciliation")
            .WithName("ImportProviderReconciliationReport")
            .WithSummary("Compare a provider CSV report with merchant payment state")
            .Accepts<string>("text/csv")
            .Produces<ReconciliationReportResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(SentinelPayPolicies.ReconciliationWrite);
        return endpoints;
    }

    private static async Task<IResult> ImportAsync(
        string provider,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        HttpContext context,
        IReconciliationImportService service,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > MaximumReportBytes)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Reconciliation report is too large");
        }

        if (context.Request.ContentType is null ||
            !context.Request.ContentType.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Content-Type must be text/csv");
        }

        var csv = await ReadBoundedUtf8Async(context.Request.Body, cancellationToken);
        if (csv is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Reconciliation report is too large");
        }
        var sourceFileName = context.Request.Headers["X-Report-Name"].ToString();
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            sourceFileName = $"{provider}-{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}.csv";
        }

        var report = await service.ImportAsync(
            new ImportReconciliationReportCommand(
                context.GetMerchantId(),
                provider,
                sourceFileName,
                periodStart,
                periodEnd,
                csv),
            cancellationToken);
        return Results.Created($"/api/v1/reconciliation/imports/{report.Id}", report);
    }

    private static async Task<string?> ReadBoundedUtf8Async(
        Stream body,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var output = new MemoryStream();
        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumReportBytes)
            {
                return null;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("Reconciliation report must be valid UTF-8.", exception);
        }
    }
}
