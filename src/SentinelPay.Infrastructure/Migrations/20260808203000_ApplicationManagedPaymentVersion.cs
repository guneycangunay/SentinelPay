using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SentinelPay.Infrastructure.Persistence;

namespace SentinelPay.Infrastructure.Migrations;

[DbContext(typeof(SentinelPayDbContext))]
[Migration("20260808203000_ApplicationManagedPaymentVersion")]
public sealed class ApplicationManagedPaymentVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE sentinelpay.payments
                ADD COLUMN "Version" bigint NOT NULL DEFAULT 0;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE sentinelpay.payments
                DROP COLUMN "Version";
            """);
    }
}
