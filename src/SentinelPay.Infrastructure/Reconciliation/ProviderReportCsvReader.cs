using System.Globalization;
using System.Text;
using SentinelPay.Domain;

namespace SentinelPay.Infrastructure.Reconciliation;

internal static class ProviderReportCsvReader
{
    private static readonly string[] ExpectedHeader =
    [
        "provider_reference",
        "authorized_amount_minor",
        "captured_amount_minor",
        "currency",
        "state",
        "occurred_at"
    ];

    public static IReadOnlyList<ProviderReportRow> Read(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            throw new DomainException("The provider reconciliation report is empty.");
        }

        if (Encoding.UTF8.GetByteCount(csv) > 2 * 1024 * 1024)
        {
            throw new DomainException("The provider reconciliation report exceeds the 2 MiB limit.");
        }

        var records = ParseRecords(csv.TrimStart('\uFEFF'));
        if (records.Count == 0 || !records[0].SequenceEqual(ExpectedHeader, StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainException($"CSV header must be: {string.Join(',', ExpectedHeader)}.");
        }

        if (records.Count > 10_001)
        {
            throw new DomainException("The provider reconciliation report exceeds 10,000 data rows.");
        }

        var rows = new List<ProviderReportRow>(records.Count - 1);
        var references = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < records.Count; index++)
        {
            var fields = records[index];
            if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }

            if (fields.Count != ExpectedHeader.Length)
            {
                throw new DomainException($"CSV row {index + 1} has {fields.Count} fields; expected {ExpectedHeader.Length}.");
            }

            var providerReference = fields[0].Trim();
            if (providerReference.Length is 0 or > 120 || !references.Add(providerReference))
            {
                throw new DomainException($"CSV row {index + 1} has a missing, oversized or duplicate provider reference.");
            }

            if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var authorized) || authorized <= 0)
            {
                throw new DomainException($"CSV row {index + 1} has an invalid authorized amount.");
            }

            if (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var captured) ||
                captured < 0 || captured > authorized)
            {
                throw new DomainException($"CSV row {index + 1} has an invalid captured amount.");
            }

            var currency = fields[3].Trim().ToUpperInvariant();
            if (currency.Length != 3 || currency.Any(character => !char.IsLetter(character)))
            {
                throw new DomainException($"CSV row {index + 1} has an invalid ISO 4217 currency.");
            }

            var state = fields[4].Trim().ToLowerInvariant();
            if (state is not (
                "requires_action" or
                "authorized" or
                "partially_captured" or
                "captured" or
                "partially_captured_and_voided" or
                "voided" or
                "partially_refunded" or
                "refunded" or
                "failed" or
                "expired"))
            {
                throw new DomainException($"CSV row {index + 1} has unsupported provider state '{state}'.");
            }

            if (!DateTimeOffset.TryParse(
                    fields[5],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var occurredAt))
            {
                throw new DomainException($"CSV row {index + 1} has an invalid occurred_at timestamp.");
            }

            rows.Add(new ProviderReportRow(
                providerReference,
                authorized,
                captured,
                currency,
                state,
                occurredAt));
        }

        return rows;
    }

    private static List<IReadOnlyList<string>> ParseRecords(string csv)
    {
        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (character == '"')
            {
                if (quoted && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (character == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            if ((character == '\r' || character == '\n') && !quoted)
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }

                fields.Add(field.ToString());
                field.Clear();
                records.Add(fields.ToArray());
                fields.Clear();
                continue;
            }

            field.Append(character);
        }

        if (quoted)
        {
            throw new DomainException("CSV contains an unterminated quoted field.");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields.ToArray());
        }

        return records;
    }
}

internal sealed record ProviderReportRow(
    string ProviderReference,
    long AuthorizedAmountMinor,
    long CapturedAmountMinor,
    string Currency,
    string State,
    DateTimeOffset OccurredAt);
