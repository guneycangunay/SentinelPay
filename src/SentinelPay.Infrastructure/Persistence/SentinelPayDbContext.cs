using Microsoft.EntityFrameworkCore;
using SentinelPay.Domain.Payments;

namespace SentinelPay.Infrastructure.Persistence;

public sealed class SentinelPayDbContext : DbContext
{
    public SentinelPayDbContext(DbContextOptions<SentinelPayDbContext> options) : base(options)
    {
    }

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WebhookReceipt> WebhookReceipts => Set<WebhookReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("sentinelpay");

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("payments");
            entity.HasKey(payment => payment.Id);
            entity.Property(payment => payment.MerchantReference).HasMaxLength(100).IsRequired();
            entity.Property(payment => payment.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            entity.Property(payment => payment.Provider).HasMaxLength(40).IsRequired();
            entity.Property(payment => payment.ProviderReference).HasMaxLength(120);
            entity.Property(payment => payment.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(payment => payment.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(payment => payment.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(payment => payment.FailureCode).HasMaxLength(80);
            entity.Property(payment => payment.FailureMessage).HasMaxLength(500);
            entity.Property(payment => payment.Version).IsRowVersion();
            entity.HasIndex(payment => payment.IdempotencyKey).IsUnique();
            entity.HasIndex(payment => payment.MerchantReference);
            entity.HasIndex(payment => new { payment.Status, payment.UpdatedAt });
            entity.HasMany(payment => payment.Refunds)
                .WithOne()
                .HasForeignKey(refund => refund.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(payment => payment.Refunds)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.ToTable("refunds");
            entity.HasKey(refund => refund.Id);
            entity.Property(refund => refund.ProviderReference).HasMaxLength(120).IsRequired();
            entity.Property(refund => refund.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(refund => refund.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.HasIndex(refund => refund.IdempotencyKey).IsUnique();
            entity.HasIndex(refund => refund.PaymentId);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(160).IsRequired();
            entity.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(message => message.LastError).HasMaxLength(2000);
            entity.HasIndex(message => new { message.ProcessedAt, message.NextAttemptAt, message.OccurredAt });
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
    }
}
