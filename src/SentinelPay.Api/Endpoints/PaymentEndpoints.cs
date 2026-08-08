using SentinelPay.Api.Security;
using SentinelPay.Application.Abstractions;
using SentinelPay.Application.Payments;

namespace SentinelPay.Api.Endpoints;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/payments")
            .WithTags("Payments");

        group.MapPost("/", CreatePaymentAsync)
            .WithName("CreatePayment")
            .WithSummary("Authorize a new payment")
            .Produces<PaymentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(SentinelPayPolicies.PaymentsWrite);

        group.MapGet("/{paymentId:guid}", GetPaymentAsync)
            .WithName("GetPayment")
            .WithSummary("Get the current payment state")
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization(SentinelPayPolicies.PaymentsRead);

        group.MapPost("/{paymentId:guid}/capture", CapturePaymentAsync)
            .WithName("CapturePayment")
            .WithSummary("Capture a previously authorized payment")
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(SentinelPayPolicies.PaymentsWrite);

        group.MapPost("/{paymentId:guid}/refunds", RefundPaymentAsync)
            .WithName("RefundPayment")
            .WithSummary("Partially or fully refund a captured payment")
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(SentinelPayPolicies.PaymentsWrite);

        endpoints.MapGet("/api/v1/providers", (IPaymentGatewayResolver resolver) =>
                Results.Ok(new { providers = resolver.GetProviderNames() }))
            .WithTags("Providers")
            .WithName("ListProviders")
            .RequireAuthorization(SentinelPayPolicies.PaymentsRead);

        endpoints.MapPost("/api/v1/webhooks/{provider}", HandleWebhookAsync)
            .WithTags("Webhooks")
            .WithName("HandleProviderWebhook")
            .WithSummary("Receive an HMAC-signed provider webhook")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return endpoints;
    }

    private static async Task<IResult> HandleWebhookAsync(
        string provider,
        HttpContext context,
        WebhookService webhookService,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var signature = context.Request.Headers["X-SentinelPay-Signature"].ToString();
        var result = await webhookService.HandleAsync(
            provider,
            payload,
            signature,
            cancellationToken);
        SetReplayHeader(context, result.IsReplay);
        return result.IsReplay ? Results.Ok() : Results.Accepted();
    }

    private static async Task<IResult> CreatePaymentAsync(
        CreatePaymentRequest request,
        HttpContext context,
        PaymentService paymentService,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = RequireIdempotencyKey(context);
        var result = await paymentService.CreateAsync(
            new CreatePaymentCommand(
                context.GetMerchantId(),
                request.MerchantReference,
                request.AmountMinor,
                request.Currency,
                request.Provider,
                request.PaymentMethodToken,
                idempotencyKey),
            cancellationToken);

        SetReplayHeader(context, result.IsReplay);
        return result.IsReplay
            ? Results.Ok(result.Payment)
            : Results.Created($"/api/v1/payments/{result.Payment.Id}", result.Payment);
    }

    private static async Task<IResult> GetPaymentAsync(
        Guid paymentId,
        HttpContext context,
        PaymentService paymentService,
        CancellationToken cancellationToken) =>
        Results.Ok(await paymentService.GetAsync(context.GetMerchantId(), paymentId, cancellationToken));

    private static async Task<IResult> CapturePaymentAsync(
        Guid paymentId,
        HttpContext context,
        PaymentService paymentService,
        CancellationToken cancellationToken)
    {
        var result = await paymentService.CaptureAsync(
            new CapturePaymentCommand(context.GetMerchantId(), paymentId, RequireIdempotencyKey(context)),
            cancellationToken);
        SetReplayHeader(context, result.IsReplay);
        return Results.Ok(result.Payment);
    }

    private static async Task<IResult> RefundPaymentAsync(
        Guid paymentId,
        RefundPaymentRequest request,
        HttpContext context,
        PaymentService paymentService,
        CancellationToken cancellationToken)
    {
        var result = await paymentService.RefundAsync(
            new RefundPaymentCommand(
                context.GetMerchantId(),
                paymentId,
                request.AmountMinor,
                RequireIdempotencyKey(context)),
            cancellationToken);
        SetReplayHeader(context, result.IsReplay);
        return Results.Ok(result.Payment);
    }

    private static string RequireIdempotencyKey(HttpContext context)
    {
        var value = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IdempotencyConflictException("The Idempotency-Key header is required.");
        }

        return value.Trim();
    }

    private static void SetReplayHeader(HttpContext context, bool isReplay)
    {
        if (isReplay)
        {
            context.Response.Headers["Idempotent-Replay"] = "true";
        }
    }
}
