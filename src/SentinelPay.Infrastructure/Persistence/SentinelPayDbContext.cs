using Microsoft.EntityFrameworkCore;
using SentinelPay.Domain.Ledger;
using SentinelPay.Domain.Merchants;
using SentinelPay.Domain.Payments;
using SentinelPay.Domain.Reconciliation;
using SentinelPay.Domain.Settlements;
using SentinelPay.Infrastructure.Security;

namespace SentinelPay.Infrastructure.Persistence;

public sealed class SentinelPayDbContext : DbContext
{
    public SentinelPayDbContext(DbContextOptions<SentinelPayDbContext> options) : base(options)
    {
    }

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Capture> Captures => Set<Capture>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<PaymentOperation> PaymentOperations => Set<PaymentOperation>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<ApiKeyCredential> ApiKeyCredentials => Set<ApiKeyCredential>();
    public DbSet<LedgerJournal> LedgerJournals => Set<LedgerJournal>();
    public DbSet<LedgerLine> LedgerLines => Set<LedgerLine>();
    public DbSet<SettlementBatch> SettlementBatches => Set<SettlementBatch>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WebhookReceipt> WebhookReceipts => Set<WebhookReceipt>();
    public DbSet<ConsumedEvent> ConsumedEvents => Set<ConsumedEvent>();
    public DbSet<ReconciliationReport> ReconciliationReports => Set<ReconciliationReport>();
    public DbSet<ReconciliationIssue> ReconciliationIssues => Set<ReconciliationIssue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("sentinelpay");

        modelBuilder.Entity<Merchant>(entity =>
        {
            entity.ToTable("merchants");
            entity.HasKey(merchant => merchant.Id);
            entity.Property(merchant => merchant.Name).HasMaxLength(160).IsRequired();
            entity.Property(merchant => merchant.Status).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<ApiKeyCredential>(entity =>
        {
            entity.ToTable("api_key_credentials");
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.Name).HasMaxLength(100).IsRequired();
            entity.Property(credential => credential.KeyHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(credential => credential.Scopes).HasMaxLength(500).IsRequired();
            entity.HasIndex(credential => credential.KeyHash).IsUnique();
            entity.HasIndex(credential => credential.MerchantId);
            entity.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(credential => credential.MerchantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("payments");
            entity.HasKey(payment => payment.Id);
            entity.Property(payment => payment.MerchantReference).HasMaxLength(100).IsRequired();
            entity.Property(payment => payment.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            entity.Property(payment => payment.Provider).HasMaxLength(40).IsRequired();
            entity.Property(payment => payment.ProviderReference).HasMaxLength(120);
            entity.Property(payment => payment.NextActionType).HasMaxLength(40);
            entity.Property(payment => payment.NextActionUrl).HasMaxLength(1000);
            entity.Property(payment => payment.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(payment => payment.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(payment => payment.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(payment => payment.FailureCode).HasMaxLength(80);
            entity.Property(payment => payment.FailureMessage).HasMaxLength(500);
            entity.Property(payment => payment.Version).IsRowVersion();
            entity.HasIndex(payment => new { payment.MerchantId, payment.IdempotencyKey }).IsUnique();
            entity.HasIndex(payment => new { payment.MerchantId, payment.MerchantReference });
            entity.HasIndex(payment => new { payment.Status, payment.UpdatedAt });
            entity.HasIndex(payment => new { payment.Provider, payment.ProviderReference })
                .IsUnique()
                .HasFilter("\"ProviderReference\" IS NOT NULL");
            entity.HasMany(payment => payment.Refunds)
                .WithOne()
                .HasForeignKey(refund => refund.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(payment => payment.Refunds)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.HasMany(payment => payment.Captures)
                .WithOne()
                .HasForeignKey(capture => capture.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(payment => payment.Captures)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.HasMany(payment => payment.Operations)
                .WithOne()
                .HasForeignKey(operation => operation.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(payment => payment.Operations)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(payment => payment.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Capture>(entity =>
        {
            entity.ToTable("captures");
            entity.HasKey(capture => capture.Id);
            entity.Property(capture => capture.ProviderReference).HasMaxLength(120).IsRequired();
            entity.Property(capture => capture.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(capture => capture.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.HasIndex(capture => new { capture.PaymentId, capture.IdempotencyKey }).IsUnique();
            entity.HasIndex(capture => new { capture.PaymentId, capture.CreatedAt });
            entity.HasIndex(capture => capture.ProviderReference).IsUnique();
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.ToTable("refunds");
            entity.HasKey(refund => refund.Id);
            entity.Property(refund => refund.ProviderReference).HasMaxLength(120).IsRequired();
            entity.Property(refund => refund.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(refund => refund.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.HasIndex(refund => new { refund.PaymentId, refund.IdempotencyKey }).IsUnique();
            entity.HasIndex(refund => refund.PaymentId);
        });

        modelBuilder.Entity<PaymentOperation>(entity =>
        {
            entity.ToTable("payment_operations");
            entity.HasKey(operation => operation.Id);
            entity.Property(operation => operation.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(operation => operation.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(operation => operation.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(operation => operation.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(operation => operation.ProviderReference).HasMaxLength(120);
            entity.Property(operation => operation.ErrorCode).HasMaxLength(80);
            entity.Property(operation => operation.ErrorMessage).HasMaxLength(500);
            entity.HasIndex(operation => new
            {
                operation.MerchantId,
                operation.Type,
                operation.IdempotencyKey
            }).IsUnique();
            entity.HasIndex(operation => operation.PaymentId);
            entity.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(operation => operation.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerJournal>(entity =>
        {
            entity.ToTable("ledger_journals");
            entity.HasKey(journal => journal.Id);
            entity.Property(journal => journal.ExternalReference).HasMaxLength(160).IsRequired();
            entity.Property(journal => journal.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            entity.Property(journal => journal.Description).HasMaxLength(240).IsRequired();
            entity.HasIndex(journal => journal.ExternalReference).IsUnique();
            entity.HasIndex(journal => new { journal.MerchantId, journal.CreatedAt });
            entity.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(journal => journal.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Payment>()
                .WithMany()
                .HasForeignKey(journal => journal.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(journal => journal.Lines)
                .WithOne()
                .HasForeignKey(line => line.JournalId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(journal => journal.Lines)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<LedgerLine>(entity =>
        {
            entity.ToTable("ledger_lines");
            entity.HasKey(line => line.Id);
            entity.Property(line => line.Account).HasConversion<string>().HasMaxLength(40);
            entity.Property(line => line.Direction).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(line => new { line.MerchantId, line.Account, line.SettlementBatchId, line.CreatedAt });
            entity.HasIndex(line => line.PaymentId);
            entity.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(line => line.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Payment>()
                .WithMany()
                .HasForeignKey(line => line.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SettlementBatch>()
                .WithMany()
                .HasForeignKey(line => line.SettlementBatchId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SettlementBatch>(entity =>
        {
            entity.ToTable("settlement_batches");
            entity.HasKey(settlement => settlement.Id);
            entity.Property(settlement => settlement.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            entity.Property(settlement => settlement.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(settlement => settlement.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(settlement => new { settlement.MerchantId, settlement.IdempotencyKey }).IsUnique();
            entity.HasIndex(settlement => new { settlement.MerchantId, settlement.Currency, settlement.CreatedAt });
            entity.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(settlement => settlement.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(160).IsRequired();
            entity.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(message => message.LastError).HasMaxLength(2000);
            entity.Property(message => message.LockedBy).HasMaxLength(160);
            entity.HasIndex(message => new
                {
                    message.NextAttemptAt,
                    message.LockedUntil,
                    message.OccurredAt
                })
                .HasFilter("\"ProcessedAt\" IS NULL AND \"DeadLetteredAt\" IS NULL");
        });

        modelBuilder.Entity<WebhookReceipt>(entity =>
        {
            entity.ToTable("webhook_receipts");
            entity.HasKey(receipt => receipt.Id);
            entity.Property(receipt => receipt.Provider).HasMaxLength(40).IsRequired();
            entity.Property(receipt => receipt.EventId).HasMaxLength(160).IsRequired();
            entity.Property(receipt => receipt.EventType).HasMaxLength(160).IsRequired();
            entity.Property(receipt => receipt.PayloadHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.HasIndex(receipt => new { receipt.Provider, receipt.EventId }).IsUnique();
        });

        modelBuilder.Entity<ConsumedEvent>(entity =>
        {
            entity.ToTable("consumed_events");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Consumer).HasMaxLength(120).IsRequired();
            entity.Property(message => message.EventId).HasMaxLength(160).IsRequired();
            entity.Property(message => message.EventType).HasMaxLength(160).IsRequired();
            entity.Property(message => message.PayloadSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.HasIndex(message => new { message.Consumer, message.EventId }).IsUnique();
            entity.HasIndex(message => new { message.EventType, message.ReceivedAt });
        });

        modelBuilder.Entity<ReconciliationReport>(entity =>
        {
            entity.ToTable("reconciliation_reports");
            entity.HasKey(report => report.Id);
            entity.Property(report => report.Provider).HasMaxLength(40).IsRequired();
            entity.Property(report => report.SourceFileName).HasMaxLength(240).IsRequired();
            entity.Property(report => report.SourceSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(report => report.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(report => new { report.MerchantId, report.Provider, report.SourceSha256 }).IsUnique();
            entity.HasIndex(report => new { report.MerchantId, report.CreatedAt });
            entity.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(report => report.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(report => report.Issues)
                .WithOne()
                .HasForeignKey(issue => issue.ReportId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(report => report.Issues)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ReconciliationIssue>(entity =>
        {
            entity.ToTable("reconciliation_issues");
            entity.HasKey(issue => issue.Id);
            entity.Property(issue => issue.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(issue => issue.ProviderReference).HasMaxLength(120).IsRequired();
            entity.Property(issue => issue.Details).HasMaxLength(1000).IsRequired();
            entity.HasIndex(issue => new { issue.ReportId, issue.Type });
            entity.HasOne<Payment>()
                .WithMany()
                .HasForeignKey(issue => issue.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
