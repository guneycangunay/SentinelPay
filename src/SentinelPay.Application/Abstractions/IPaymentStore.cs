using SentinelPay.Domain.Payments;

namespace SentinelPay.Application.Abstractions;

public interface IPaymentStore
{
    Task<Payment?> GetAsync(Guid paymentId, CancellationToken cancellationToken);
    Task<Payment?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task<Payment?> GetByProviderReferenceAsync(string provider, string providerReference, CancellationToken cancellationToken);
    Task AddAsync(Payment payment, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
