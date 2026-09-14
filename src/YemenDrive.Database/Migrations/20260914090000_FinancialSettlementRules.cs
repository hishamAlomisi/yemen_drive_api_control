using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace YemenDrive.Database.Migrations;

[DbContext(typeof(YemenDriveDbContext))]
[Migration("20260914090000_FinancialSettlementRules")]
public partial class FinancialSettlementRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "DriverCommissionRate",
            table: "PricingRules",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(
            name: "DriverCommissionFixed",
            table: "PricingRules",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(
            name: "CancellationFee",
            table: "PricingRules",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(
            name: "DriverCommissionAmount",
            table: "Rides",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);
        migrationBuilder.AddCheckConstraint(
            name: "CK_Rides_DriverCommissionAmount_NonNegative",
            table: "Rides",
            sql: "[DriverCommissionAmount] >= 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Rides_DriverCommissionAmount_NonNegative",
            table: "Rides");
        migrationBuilder.DropColumn(name: "DriverCommissionRate", table: "PricingRules");
        migrationBuilder.DropColumn(name: "DriverCommissionFixed", table: "PricingRules");
        migrationBuilder.DropColumn(name: "CancellationFee", table: "PricingRules");
        migrationBuilder.DropColumn(name: "DriverCommissionAmount", table: "Rides");
    }
}
