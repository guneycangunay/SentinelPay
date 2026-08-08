using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Migrations;

[DbContext(typeof(SentinelPayDbContext))]
[Migration("20260808110000_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SCHEMA IF NOT EXISTS sentinelpay;

            CREATE TABLE sentinelpay.merchants (
                "Id" uuid PRIMARY KEY,
                "Name" varchar(160) NOT NULL,
                "Status" varchar(32) NOT NULL,
                "CreatedAt" timestamptz NOT NULL
            );

            CREATE TABLE sentinelpay.api_key_credentials (
                "Id" uuid PRIMARY KEY,
                "MerchantId" uuid NOT NULL REFERENCES sentinelpay.merchants("Id") ON DELETE CASCADE,
                "Name" varchar(100) NOT NULL,
                "KeyHash" char(64) NOT NULL,
                "Scopes" varchar(500) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "ExpiresAt" timestamptz NULL,
                "RevokedAt" timestamptz NULL,
                CONSTRAINT "UQ_api_key_credentials_KeyHash" UNIQUE ("KeyHash")
            );
            CREATE INDEX "IX_api_key_credentials_MerchantId"
                ON sentinelpay.api_key_credentials("MerchantId");

            CREATE TABLE sentinelpay.payments (
                "Id" uuid PRIMARY KEY,
                "MerchantId" uuid NOT NULL REFERENCES sentinelpay.merchants("Id") ON DELETE RESTRICT,
                "MerchantReference" varchar(100) NOT NULL,
                "AmountMinor" bigint NOT NULL CHECK ("AmountMinor" > 0),
                "Currency" char(3) NOT NULL,
                "Provider" varchar(40) NOT NULL,
                "ProviderReference" varchar(120) NULL,
                "Status" varchar(32) NOT NULL,
                "IdempotencyKey" varchar(128) NOT NULL,
                "RequestHash" char(64) NOT NULL,
                "CapturedAmountMinor" bigint NOT NULL DEFAULT 0,
                "RefundedAmountMinor" bigint NOT NULL DEFAULT 0,
                "FailureCode" varchar(80) NULL,
                "FailureMessage" varchar(500) NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                "AuthorizedAt" timestamptz NULL,
                "CapturedAt" timestamptz NULL,
                CONSTRAINT "CK_payments_amounts" CHECK (
                    "CapturedAmountMinor" >= 0 AND
                    "RefundedAmountMinor" >= 0 AND
                    "RefundedAmountMinor" <= "CapturedAmountMinor"
                ),
                CONSTRAINT "UQ_payments_Merchant_Idempotency"
                    UNIQUE ("MerchantId", "IdempotencyKey")
            );
            CREATE INDEX "IX_payments_MerchantReference"
                ON sentinelpay.payments("MerchantId", "MerchantReference");
            CREATE INDEX "IX_payments_Status_UpdatedAt"
                ON sentinelpay.payments("Status", "UpdatedAt");
            CREATE UNIQUE INDEX "IX_payments_ProviderReference"
                ON sentinelpay.payments("Provider", "ProviderReference")
                WHERE "ProviderReference" IS NOT NULL;

            CREATE TABLE sentinelpay.payment_operations (
                "Id" uuid PRIMARY KEY,
                "MerchantId" uuid NOT NULL REFERENCES sentinelpay.merchants("Id") ON DELETE RESTRICT,
                "PaymentId" uuid NOT NULL REFERENCES sentinelpay.payments("Id") ON DELETE CASCADE,
                "Type" varchar(32) NOT NULL,
                "IdempotencyKey" varchar(128) NOT NULL,
                "RequestHash" char(64) NOT NULL,
                "Status" varchar(32) NOT NULL,
                "ProviderReference" varchar(120) NULL,
                "ErrorCode" varchar(80) NULL,
                "ErrorMessage" varchar(500) NULL,
                "StartedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                "CompletedAt" timestamptz NULL,
                CONSTRAINT "UQ_payment_operations_Merchant_Type_Key"
                    UNIQUE ("MerchantId", "Type", "IdempotencyKey")
            );
            CREATE INDEX "IX_payment_operations_PaymentId"
                ON sentinelpay.payment_operations("PaymentId");

            CREATE TABLE sentinelpay.refunds (
                "Id" uuid PRIMARY KEY,
                "PaymentId" uuid NOT NULL REFERENCES sentinelpay.payments("Id") ON DELETE CASCADE,
                "AmountMinor" bigint NOT NULL CHECK ("AmountMinor" > 0),
                "ProviderReference" varchar(120) NOT NULL,
                "IdempotencyKey" varchar(128) NOT NULL,
                "RequestHash" char(64) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                CONSTRAINT "UQ_refunds_Payment_Idempotency"
                    UNIQUE ("PaymentId", "IdempotencyKey")
            );
            CREATE INDEX "IX_refunds_PaymentId" ON sentinelpay.refunds("PaymentId");

            CREATE TABLE sentinelpay.settlement_batches (
                "Id" uuid PRIMARY KEY,
                "MerchantId" uuid NOT NULL REFERENCES sentinelpay.merchants("Id") ON DELETE RESTRICT,
                "Currency" char(3) NOT NULL,
                "AmountMinor" bigint NOT NULL CHECK ("AmountMinor" > 0),
                "IdempotencyKey" varchar(128) NOT NULL,
                "PeriodEnd" timestamptz NOT NULL,
                "Status" varchar(32) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "PaidAt" timestamptz NULL,
                CONSTRAINT "UQ_settlement_batches_Merchant_Idempotency"
                    UNIQUE ("MerchantId", "IdempotencyKey")
            );
            CREATE INDEX "IX_settlement_batches_Merchant_Currency_CreatedAt"
                ON sentinelpay.settlement_batches("MerchantId", "Currency", "CreatedAt");

            CREATE TABLE sentinelpay.ledger_journals (
                "Id" uuid PRIMARY KEY,
                "MerchantId" uuid NOT NULL REFERENCES sentinelpay.merchants("Id") ON DELETE RESTRICT,
                "PaymentId" uuid NULL REFERENCES sentinelpay.payments("Id") ON DELETE RESTRICT,
                "ExternalReference" varchar(160) NOT NULL,
                "Currency" char(3) NOT NULL,
                "Description" varchar(240) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                CONSTRAINT "UQ_ledger_journals_ExternalReference" UNIQUE ("ExternalReference")
            );
            CREATE INDEX "IX_ledger_journals_Merchant_CreatedAt"
                ON sentinelpay.ledger_journals("MerchantId", "CreatedAt");

            CREATE TABLE sentinelpay.ledger_lines (
                "Id" uuid PRIMARY KEY,
                "JournalId" uuid NOT NULL REFERENCES sentinelpay.ledger_journals("Id") ON DELETE CASCADE,
                "MerchantId" uuid NOT NULL REFERENCES sentinelpay.merchants("Id") ON DELETE RESTRICT,
                "PaymentId" uuid NULL REFERENCES sentinelpay.payments("Id") ON DELETE RESTRICT,
                "SettlementBatchId" uuid NULL REFERENCES sentinelpay.settlement_batches("Id") ON DELETE RESTRICT,
                "Account" varchar(40) NOT NULL,
                "Direction" varchar(16) NOT NULL,
                "AmountMinor" bigint NOT NULL CHECK ("AmountMinor" > 0),
                "CreatedAt" timestamptz NOT NULL
            );
            CREATE INDEX "IX_ledger_lines_SettlementLookup"
                ON sentinelpay.ledger_lines("MerchantId", "Account", "SettlementBatchId", "CreatedAt");
            CREATE INDEX "IX_ledger_lines_PaymentId" ON sentinelpay.ledger_lines("PaymentId");

            CREATE TABLE sentinelpay.outbox_messages (
                "Id" uuid PRIMARY KEY,
                "EventType" varchar(160) NOT NULL,
                "AggregateId" uuid NOT NULL,
                "Payload" jsonb NOT NULL,
                "OccurredAt" timestamptz NOT NULL,
                "ProcessedAt" timestamptz NULL,
                "DeadLetteredAt" timestamptz NULL,
                "NextAttemptAt" timestamptz NULL,
                "AttemptCount" integer NOT NULL DEFAULT 0,
                "LastError" varchar(2000) NULL,
                "LockedBy" varchar(160) NULL,
                "LockedUntil" timestamptz NULL
            );
            CREATE INDEX "IX_outbox_messages_Dispatch"
                ON sentinelpay.outbox_messages("NextAttemptAt", "LockedUntil", "OccurredAt")
                WHERE "ProcessedAt" IS NULL AND "DeadLetteredAt" IS NULL;

            CREATE TABLE sentinelpay.webhook_receipts (
                "Id" uuid PRIMARY KEY,
                "Provider" varchar(40) NOT NULL,
                "EventId" varchar(160) NOT NULL,
                "EventType" varchar(160) NOT NULL,
                "PayloadHash" char(64) NOT NULL,
                "ReceivedAt" timestamptz NOT NULL,
                CONSTRAINT "UQ_webhook_receipts_Provider_EventId" UNIQUE ("Provider", "EventId")
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS sentinelpay CASCADE;");
    }
}
