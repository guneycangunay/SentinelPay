using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SentinelPay.Api.Endpoints;
using SentinelPay.Api.Infrastructure;
using SentinelPay.Api.Security;
using SentinelPay.Infrastructure;
using SentinelPay.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services
    .AddAuthentication(ApiKeyAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, MerchantApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(SentinelPayPolicies.PaymentsRead, policy => policy.RequireClaim("scope", SentinelPayPolicies.PaymentsRead))
    .AddPolicy(SentinelPayPolicies.PaymentsWrite, policy => policy.RequireClaim("scope", SentinelPayPolicies.PaymentsWrite))
    .AddPolicy(SentinelPayPolicies.LedgerRead, policy => policy.RequireClaim("scope", SentinelPayPolicies.LedgerRead))
    .AddPolicy(SentinelPayPolicies.SettlementsRead, policy => policy.RequireClaim("scope", SentinelPayPolicies.SettlementsRead))
    .AddPolicy(SentinelPayPolicies.SettlementsWrite, policy => policy.RequireClaim("scope", SentinelPayPolicies.SettlementsWrite));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(ApiKeyAuthenticationDefaults.MerchantIdClaim)?.Value ??
            context.Connection.RemoteIpAddress?.ToString() ??
            "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "SentinelPay.Api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"))
    .WithTracing(tracing =>
    {
        tracing.AddSource(PaymentTelemetry.ActivitySourceName);
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(PaymentTelemetry.MeterName, "SentinelPay.Outbox");
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        metrics.AddRuntimeInstrumentation();
        metrics.AddPrometheusExporter();
    });

builder.Services.AddSentinelPayInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

app.MapGet("/", (IHostEnvironment environment) => Results.Ok(new
    {
        service = "SentinelPay.Api",
        version = typeof(Program).Assembly.GetName().Version?.ToString(),
        environment = environment.EnvironmentName,
        health = "/health/ready",
        documentation = environment.IsDevelopment() ? "/swagger" : null
    }))
    .ExcludeFromDescription();
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }))
    .WithTags("Operations");
app.MapGet("/health/ready", async (SentinelPayDbContext dbContext, CancellationToken cancellationToken) =>
    await dbContext.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable"))
    .WithTags("Operations");
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapPaymentEndpoints();
app.MapFinanceEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapDevelopmentEndpoints();
}

await app.RunAsync();

public partial class Program;
