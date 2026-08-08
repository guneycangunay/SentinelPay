using Microsoft.EntityFrameworkCore;
using SentinelPay.Application.Abstractions;
using SentinelPay.Domain.Payments;

namespace SentinelPay.Infrastructure.Persistence;

public sealed class PaymentStore : IPaymentStore
{
    private readonly SentinelPayDbContext _dbContext;

    public PaymentStore(SentinelPayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Payment?> GetAsync(Guid paymentId, CancellationToken cancellationToken) =>
        _dbContext.Payments
            .Include(payment => payment.Refunds)
            .SingleOrDefaultAsync(payment => payment.Id == paymentId, cancellationToken);

    public Task<Payment?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        _dbContext.Payments
            .Include(payment => payment.Refunds)
            .SingleOrDefaultAsync(payment => payment.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<Payment?> GetByProviderReferenceAsync(
        string provider,
        string providerReference,
        CancellationToken cancellationToken) =>
        _dbContext.Payments
            .Include(payment => payment.Refunds)
            .SingleOrDefaultAsync(
                payment => payment.Provider == provider && payment.ProviderReference == providerReference,
                cancellationToken);

    public async Task AddAsync(Payment payment, CancellationToken cancellationToken)
    {
        await _dbContext.Payments.AddAsync(payment, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
