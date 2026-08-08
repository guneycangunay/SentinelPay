using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SentinelPay.Application.Payments;

public static class PaymentTelemetry
{
    public const string ActivitySourceName = "SentinelPay.Payments";
    public const string MeterName = "SentinelPay.Payments";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Authorized = Meter.CreateCounter<long>("sentinelpay.payments.authorized");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("sentinelpay.payments.failed");
    private static readonly Counter<long> Captured = Meter.CreateCounter<long>("sentinelpay.payments.captured");
    private static readonly Counter<long> Refunded = Meter.CreateCounter<long>("sentinelpay.payments.refunded");
    private static readonly Histogram<double> ProviderLatency =
        Meter.CreateHistogram<double>("sentinelpay.provider.duration", "ms");
    private static readonly Histogram<long> Amounts =
        Meter.CreateHistogram<long>("sentinelpay.payment.amount", "minor_units");

    public static void RecordAuthorization(string provider, string currency, long amountMinor, bool successful)
    {
        var tags = new TagList
        {
            { "provider", provider },
            { "currency", currency }
        };
        (successful ? Authorized : Failed).Add(1, tags);
        Amounts.Record(amountMinor, tags);
    }

    public static void RecordCapture(string provider, string currency, long amountMinor)
    {
        var tags = new TagList { { "provider", provider }, { "currency", currency } };
        Captured.Add(1, tags);
        Amounts.Record(amountMinor, tags);
    }

    public static void RecordRefund(string provider, string currency, long amountMinor)
    {
        var tags = new TagList { { "provider", provider }, { "currency", currency } };
        Refunded.Add(1, tags);
        Amounts.Record(amountMinor, tags);
    }

    public static void RecordProviderLatency(string provider, string operation, TimeSpan duration) =>
        ProviderLatency.Record(
            duration.TotalMilliseconds,
            new TagList { { "provider", provider }, { "operation", operation } });
}
