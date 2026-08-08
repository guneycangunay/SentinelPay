using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Merchants;
using SentinelPay.Infrastructure.Security;

namespace SentinelPay.Infrastructure.Persistence;

public sealed class DatabaseInitializer
{
    private readonly SentinelPayDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;

    public DatabaseInitializer(
        SentinelPayDbContext dbContext,
        IConfiguration configuration,
        IClock clock)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _clock = clock;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.Database.MigrateAsync(cancellationToken);

        if (!_configuration.GetValue("DevelopmentMerchant:Seed", false))
        {
            return;
        }

        var merchantId = _configuration.GetValue<Guid>("DevelopmentMerchant:Id");
        var name = _configuration["DevelopmentMerchant:Name"] ?? "SentinelPay Demo Merchant";
        var apiKey = _configuration["DevelopmentMerchant:ApiKey"]
            ?? throw new InvalidOperationException("DevelopmentMerchant:ApiKey is required when seeding is enabled.");

        if (!await _dbContext.Merchants.AnyAsync(merchant => merchant.Id == merchantId, cancellationToken))
        {
            await _dbContext.Merchants.AddAsync(
                Merchant.Create(merchantId, name, _clock.UtcNow),
                cancellationToken);
        }

        var keyHash = ApiKeyHasher.Hash(apiKey);
        if (!await _dbContext.ApiKeyCredentials.AnyAsync(
                credential => credential.KeyHash == keyHash,
                cancellationToken))
        {
            await _dbContext.ApiKeyCredentials.AddAsync(new ApiKeyCredential
            {
                Id = Guid.NewGuid(),
                MerchantId = merchantId,
                Name = "development-full-access",
                KeyHash = keyHash,
                Scopes = "payments:read payments:write ledger:read settlements:read settlements:write",
                CreatedAt = _clock.UtcNow
            }, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
