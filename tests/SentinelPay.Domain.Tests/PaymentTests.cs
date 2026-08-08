using SentinelPay.Domain;
using SentinelPay.Domain.Payments;

namespace SentinelPay.Domain.Tests;

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_NormalizesCurrencyAndProvider()
    {
        var payment = CreatePayment(currency: "eur", provider: "Mock-Bank");

        Assert.Equal("EUR", payment.Currency);
        Assert.Equal("mock-bank", payment.Provider);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(12_990, payment.AmountMinor);
    }

    [Fact]
    public void Create_RejectsNonPositiveAmount()
    {
        var exception = Assert.Throws<DomainException>(() => CreatePayment(amountMinor: 0));

        Assert.Contains("greater than zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capture_RequiresAuthorizedState()
    {
        var payment = CreatePayment();

        Assert.Throws<DomainException>(() => payment.Capture(payment.AmountMinor, Now));
    }

    [Fact]
    public void Payment_SupportsAuthorizeCaptureAndPartialRefundLifecycle()
    {
        var payment = CreatePayment();
        payment.MarkAuthorized("auth_123", Now.AddSeconds(1));
        payment.Capture(payment.AmountMinor, Now.AddSeconds(2));

        payment.RegisterRefund(
            Guid.NewGuid(),
            2_000,
            "ref_123",
            "refund-key-0001",
            new string('A', 64),
            Now.AddSeconds(3));

        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(2_000, payment.RefundedAmountMinor);
        Assert.Single(payment.Refunds);
    }

    [Fact]
    public void Refund_TransitionsToRefundedWhenEntireCaptureIsReturned()
    {
        var payment = CreatePayment();
        payment.MarkAuthorized("auth_123", Now.AddSeconds(1));
        payment.Capture(payment.AmountMinor, Now.AddSeconds(2));

        payment.RegisterRefund(
            Guid.NewGuid(),
            payment.AmountMinor,
            "ref_full",
            "refund-key-full",
            new string('B', 64),
            Now.AddSeconds(3));

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(payment.CapturedAmountMinor, payment.RefundedAmountMinor);
    }

    private static Payment CreatePayment(
        long amountMinor = 12_990,
        string currency = "EUR",
        string provider = "mock-bank") =>
        Payment.Create(
            "order-0001",
            amountMinor,
            currency,
            provider,
            "create-key-0001",
            new string('C', 64),
            Now);
}
