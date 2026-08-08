using SentinelPay.Domain;
using SentinelPay.Domain.Payments;

namespace SentinelPay.Domain.Tests;

public sealed class PaymentOperationTests
{
    private static readonly Guid MerchantId = Guid.Parse("2dc5f437-0a11-4c67-a810-b3e784470f73");
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartOperation_PersistsAttemptBeforeCompletion()
    {
        var payment = CreatePayment();

        var operation = payment.StartOperation(
            PaymentOperationType.Authorize,
            "operation-key-0001",
            new string('A', 64),
            Now);

        Assert.Equal(PaymentOperationStatus.Started, operation.Status);
        Assert.Null(operation.CompletedAt);
        Assert.Single(payment.Operations);
    }

    [Fact]
    public void Operation_CannotCompleteTwice()
    {
        var operation = CreatePayment().StartOperation(
            PaymentOperationType.Capture,
            "operation-key-0002",
            new string('B', 64),
            Now);
        operation.Succeed("provider-ref", Now.AddSeconds(1));

        Assert.Throws<DomainException>(() => operation.Fail("late", "Late failure", Now.AddSeconds(2)));
    }

    [Fact]
    public void Payment_RejectsDuplicateOperationKeyForSameType()
    {
        var payment = CreatePayment();
        payment.StartOperation(
            PaymentOperationType.Refund,
            "operation-key-0003",
            new string('C', 64),
            Now);

        Assert.Throws<DomainException>(() => payment.StartOperation(
            PaymentOperationType.Refund,
            "operation-key-0003",
            new string('C', 64),
            Now));
    }

    private static Payment CreatePayment() => Payment.Create(
        MerchantId,
        "order-operation-1",
        12_990,
        "EUR",
        "mock-bank",
        "create-operation-0001",
        new string('D', 64),
        Now);
}
