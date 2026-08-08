using SentinelPay.Api.Security;
using SentinelPay.Application.Abstractions;

namespace SentinelPay.Api.Endpoints;

public static class DevelopmentEndpoints
{
    public static IEndpointRouteBuilder MapDevelopmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/dev/provider-state", (
                SetProviderStateRequest request,
                ISandboxGatewayControl gatewayControl) =>
            {
                gatewayControl.SetState(
                    request.ProviderReference,
                    request.State,
                    request.ErrorCode,
                    request.ErrorMessage);
                return Results.Accepted();
            })
            .WithTags("Development")
            .WithName("SetSandboxProviderState")
            .WithSummary("Inject provider state drift for reconciliation tests")
            .RequireAuthorization(SentinelPayPolicies.PaymentsWrite);

        return endpoints;
    }
}

public sealed record SetProviderStateRequest(
    string ProviderReference,
    GatewayPaymentState State,
    string? ErrorCode,
    string? ErrorMessage);
