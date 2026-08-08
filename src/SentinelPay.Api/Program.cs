using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SentinelPay.Api.Endpoints;
using SentinelPay.Api.Infrastructure;
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

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
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
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        metrics.AddRuntimeInstrumentation();
        metrics.AddPrometheusExporter();
    });

builder.Services.AddSentinelPayInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<SentinelPayDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

app.MapGet("/", () => Results.Redirect("/swagger"))
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

await app.RunAsync();

public partial class Program;
