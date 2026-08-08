namespace SentinelPay.Domain.Payments;

public enum PaymentStatus
{
    Pending = 0,
    Authorized = 1,
    Captured = 2,
    PartiallyRefunded = 3,
    Refunded = 4,
    Failed = 5,
    RequiresAction = 6,
    PartiallyCaptured = 7,
    PartiallyCapturedAndVoided = 8,
    Voided = 9,
    Expired = 10
}
