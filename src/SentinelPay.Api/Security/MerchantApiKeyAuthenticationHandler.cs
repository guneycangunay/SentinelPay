using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentinelPay.Domain.Merchants;
using SentinelPay.Infrastructure.Persistence;
using SentinelPay.Infrastructure.Security;

namespace SentinelPay.Api.Security;

public sealed class MerchantApiKeyAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly SentinelPayDbContext _dbContext;

    public MerchantApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SentinelPayDbContext dbContext)
        : base(options, logger, encoder)
    {
        _dbContext = dbContext;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var apiKey = Request.Headers[ApiKeyAuthenticationDefaults.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticateResult.NoResult();
        }

        var keyHash = ApiKeyHasher.Hash(apiKey);
        var credential = await _dbContext.ApiKeyCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.KeyHash == keyHash, Context.RequestAborted);
        if (credential is null ||
            credential.RevokedAt is not null ||
            credential.ExpiresAt <= TimeProvider.GetUtcNow())
        {
            return AuthenticateResult.Fail("API key is invalid, expired or revoked.");
        }

        var merchant = await _dbContext.Merchants
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == credential.MerchantId, Context.RequestAborted);
        if (merchant is null || merchant.Status != MerchantStatus.Active)
        {
            return AuthenticateResult.Fail("Merchant is unavailable.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, merchant.Id.ToString()),
            new(ClaimTypes.Name, merchant.Name),
            new(ApiKeyAuthenticationDefaults.MerchantIdClaim, merchant.Id.ToString())
        };
        claims.AddRange(credential.Scopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(scope => new Claim("scope", scope)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
