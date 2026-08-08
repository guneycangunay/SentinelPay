using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Migrations;

[DbContext(typeof(SentinelPayDbContext))]
[Migration("20260808190000_PaymentInterviewEdition")]
public sealed class PaymentInterviewEdition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE sentinelpay.payments
                ADD COLUMN "VoidedAmountMinor" bigint NOT NULL DEFAULT 0,
                ADD COLUMN "NextActionType" varchar(40) NULL,
                ADD COLUMN "NextActionUrl" varchar(1000) NULL,
                ADD COLUMN "ActionExpiresAt" timestamptz NULL,
                ADD COLUMN "AuthorizationExpiresAt" timestamptz NULL,
                ADD COLUMN "AuthorizationClosedAt" timestamptz NULL;

            ALTER TABLE sentinelpay.payments DROP CONSTRAINT "CK_payments_amounts";
            ALTER TABLE sentinelpay.payments ADD CONSTRAINT "CK_payments_amounts" CHECK (
                "CapturedAmountMinor" >= 0 AND
                "RefundedAmountMinor" >= 0 AND
                "VoidedAmountMinor" >= 0 AND
                "RefundedAmountMinor" <= "CapturedAmountMinor" AND
                "CapturedAmountMinor" + "VoidedAmountMinor" <= "AmountMinor"
            );

            CREATE TABLE sentinelpay.captures (
                "Id" uuid PRIMARY KEY,
                "PaymentId" uuid NOT NULL REFERENCES sentinelpay.payments("Id") ON DELETE CASCADE,
                "AmountMinor" bigint NOT NULL CHECK ("AmountMinor" > 0),
                "ProviderReference" varchar(120) NOT NULL,
                "IdempotencyKey" varchar(128) NOT NULL,
                "RequestHash" char(64) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                CONSTRAINT "UQ_captures_Payment_Idempotency" UNIQUE ("PaymentId", "IdempotencyKey"),
                CONSTRAINT "UQ_captures_ProviderReference" UNIQUE ("ProviderReference")
            );
            CREATE INDEX "IX_captures_Payment_CreatedAt"
                ON sentinelpay.captures("PaymentId", "CreatedAt");

            CREATE TABLE sentinelpay.consumed_events (
                "Id" uuid PRIMARY KEY,
                "Consumer" varchar(120) NOT NULL,
                "EventId" varchar(160) NOT NULL,
                "EventType" varchar(160) NOT NULL,
                "AggregateId" uuid NULL,
                "PayloadSha256" char(64) NOT NULL,
                "ReceivedAt" timestamptz NOT NULL,
                CONSTRAINT "UQ_consumed_events_Consumer_EventId" UNIQUE ("Consumer", "EventId")
            );
            CREATE INDEX "IX_consumed_events_Type_ReceivedAt"
                ON sentinelpay.consumed_events("EventType", "ReceivedAt");

            CREATE TABLE sentinelpay.reconciliation_reports (
                "Id" uuid PRIMARY KEY,
                "MerchantId" uuid NOT NULL REFERENCES sentinelpay.merchants("Id") ON DELETE RESTRICT,
                "Provider" varchar(40) NOT NULL,
                "SourceFileName" varchar(240) NOT NULL,
                "SourceSha256" char(64) NOT NULL,
                "PeriodStart" timestamptz NOT NULL,
                "PeriodEnd" timestamptz NOT NULL,
                "ProviderRowCount" integer NOT NULL,
                "MatchedRowCount" integer NOT NULL,
                "Status" varchar(32) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                CONSTRAINT "CK_reconciliation_reports_period" CHECK ("PeriodEnd" > "PeriodStart"),
                CONSTRAINT "UQ_reconciliation_reports_Source"
                    UNIQUE ("MerchantId", "Provider", "SourceSha256")
            );
            CREATE INDEX "IX_reconciliation_reports_Merchant_CreatedAt"
                ON sentinelpay.reconciliation_reports("MerchantId", "CreatedAt");

            CREATE TABLE sentinelpay.reconciliation_issues (
                "Id" uuid PRIMARY KEY,
                "ReportId" uuid NOT NULL REFERENCES sentinelpay.reconciliation_reports("Id") ON DELETE CASCADE,
                "Type" varchar(40) NOT NULL,
                "ProviderReference" varchar(120) NOT NULL,
                "PaymentId" uuid NULL REFERENCES sentinelpay.payments("Id") ON DELETE RESTRICT,
                "Details" varchar(1000) NOT NULL,
                "CreatedAt" timestamptz NOT NULL
            );
            CREATE INDEX "IX_reconciliation_issues_Report_Type"
                ON sentinelpay.reconciliation_issues("ReportId", "Type");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS sentinelpay.reconciliation_issues;
            DROP TABLE IF EXISTS sentinelpay.reconciliation_reports;
            DROP TABLE IF EXISTS sentinelpay.consumed_events;
            DROP TABLE IF EXISTS sentinelpay.captures;

            ALTER TABLE sentinelpay.payments DROP CONSTRAINT "CK_payments_amounts";
            ALTER TABLE sentinelpay.payments ADD CONSTRAINT "CK_payments_amounts" CHECK (
                "CapturedAmountMinor" >= 0 AND
                "RefundedAmountMinor" >= 0 AND
                "RefundedAmountMinor" <= "CapturedAmountMinor"
            );
            ALTER TABLE sentinelpay.payments
                DROP COLUMN "VoidedAmountMinor",
                DROP COLUMN "NextActionType",
                DROP COLUMN "NextActionUrl",
                DROP COLUMN "ActionExpiresAt",
                DROP COLUMN "AuthorizationExpiresAt",
                DROP COLUMN "AuthorizationClosedAt";
            """);
    }
}
