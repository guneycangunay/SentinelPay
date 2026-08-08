using SentinelPay.Domain;
using SentinelPay.Domain.Payments;

namespace SentinelPay.Domain.Tests;

public sealed class PaymentTests
{
    private static readonly Guid MerchantId = Guid.Parse("2dc5f437-0a11-4c67-a810-b3e784470f73");
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

        Assert.Throws<DomainException>(() => payment.EnsureCanCapture(payment.AmountMinor, Now));
    }

    [Fact]
    public void Payment_SupportsAuthorizeCaptureAndPartialRefundLifecycle()
    {
        var payment = CreatePayment();
        payment.MarkAuthorized("auth_123", Now.AddDays(7), Now.AddSeconds(1));
        payment.RegisterCapture(
            Guid.NewGuid(),
            payment.AmountMinor,
            "cap_123",
            "capture-key-0001",
            new string('D', 64),
            Now.AddSeconds(2));

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
        payment.MarkAuthorized("auth_123", Now.AddDays(7), Now.AddSeconds(1));
        payment.RegisterCapture(
            Guid.NewGuid(),
            payment.AmountMinor,
            "cap_full",
            "capture-key-full",
            new string('D', 64),
            Now.AddSeconds(2));

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

    [Fact]
    public void Refund_RejectsAmountAboveRemainingBalanceWithoutMutation()
    {
        var payment = CreatePayment();
        payment.MarkAuthorized("auth_123", Now.AddDays(7), Now.AddSeconds(1));
        payment.RegisterCapture(
            Guid.NewGuid(),
            payment.AmountMinor,
            "cap_refund_limit",
            "capture-key-limit",
            new string('D', 64),
            Now.AddSeconds(2));

        Assert.Throws<DomainException>(() => payment.EnsureCanRefund(payment.AmountMinor + 1));
        Assert.Equal(PaymentStatus.Captured, payment.Status);
        Assert.Equal(0, payment.RefundedAmountMinor);
        Assert.Empty(payment.Refunds);
    }

    [Fact]
    public void Payment_SupportsMultipleCaptureThenVoidsRemainder()
    {
        var payment = CreatePayment(amountMinor: 10_000);
        payment.MarkAuthorized("auth_multi", Now.AddDays(7), Now.AddSeconds(1));

        payment.RegisterCapture(
            Guid.NewGuid(),
            3_000,
            "cap_multi_1",
            "capture-multi-0001",
            new string('D', 64),
            Now.AddSeconds(2));
        payment.RegisterCapture(
            Guid.NewGuid(),
            2_000,
            "cap_multi_2",
            "capture-multi-0002",
            new string('E', 64),
            Now.AddSeconds(3));
        payment.VoidRemainingAuthorization(Now.AddSeconds(4));

        Assert.Equal(PaymentStatus.PartiallyCapturedAndVoided, payment.Status);
        Assert.Equal(5_000, payment.CapturedAmountMinor);
        Assert.Equal(5_000, payment.VoidedAmountMinor);
        Assert.Equal(0, payment.RemainingAuthorizedAmountMinor);
        Assert.Equal(2, payment.Captures.Count);
    }

    [Fact]
    public void Payment_RejectsCaptureAboveAuthorizationRemainder()
    {
        var payment = CreatePayment(amountMinor: 10_000);
        payment.MarkAuthorized("auth_limit", Now.AddDays(7), Now.AddSeconds(1));
        payment.RegisterCapture(
            Guid.NewGuid(),
            7_000,
            "cap_limit_1",
            "capture-limit-0001",
            new string('D', 64),
            Now.AddSeconds(2));

        Assert.Throws<DomainException>(() => payment.EnsureCanCapture(3_001, Now.AddSeconds(3)));
        Assert.Equal(3_000, payment.RemainingAuthorizedAmountMinor);
    }

    [Fact]
    public void Payment_TracksThreeDsChallengeWithoutStoringCardData()
    {
        var payment = CreatePayment();
        payment.MarkAuthenticationRequired(
            "auth_3ds",
            "redirect",
            "https://sandbox.sentinelpay.dev/3ds/auth_3ds",
            Now.AddMinutes(10),
            Now.AddSeconds(1));

        Assert.Equal(PaymentStatus.RequiresAction, payment.Status);
        Assert.Equal("redirect", payment.NextActionType);

        payment.MarkAuthorized("auth_3ds", Now.AddDays(7), Now.AddSeconds(2));

        Assert.Equal(PaymentStatus.Authorized, payment.Status);
        Assert.Null(payment.NextActionUrl);
    }

    [Fact]
    public void ExpiredPartialAuthorization_ClosesOnlyUncapturedRemainder()
    {
        var payment = CreatePayment(amountMinor: 10_000);
        payment.MarkAuthorized("auth_expiry", Now.AddMinutes(5), Now.AddSeconds(1));
        payment.RegisterCapture(
            Guid.NewGuid(),
            4_000,
            "cap_expiry",
            "capture-expiry-0001",
            new string('F', 64),
            Now.AddSeconds(2));

        payment.Expire(Now.AddMinutes(6));

        Assert.Equal(PaymentStatus.PartiallyCapturedAndVoided, payment.Status);
        Assert.Equal(4_000, payment.CapturedAmountMinor);
        Assert.Equal(6_000, payment.VoidedAmountMinor);
        Assert.Equal(0, payment.RemainingAuthorizedAmountMinor);
    }

    [Fact]
    public void ExpiredThreeDsAction_DoesNotPretendFundsWereAuthorizedOrVoided()
    {
        var payment = CreatePayment();
        payment.MarkAuthenticationRequired(
            "auth_3ds_expired",
            "redirect",
            "https://sandbox.sentinelpay.dev/3ds/auth_3ds_expired",
            Now.AddMinutes(5),
            Now);

        payment.Expire(Now.AddMinutes(6));

        Assert.Equal(PaymentStatus.Expired, payment.Status);
        Assert.Equal(0, payment.VoidedAmountMinor);
        Assert.Equal(0, payment.RemainingAuthorizedAmountMinor);
    }

    private static Payment CreatePayment(
        long amountMinor = 12_990,
        string currency = "EUR",
        string provider = "mock-bank") =>
        Payment.Create(
            MerchantId,
            "order-0001",
            amountMinor,
            currency,
            provider,
            "create-key-0001",
            new string('C', 64),
            Now);
}
