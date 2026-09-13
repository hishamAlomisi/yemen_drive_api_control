using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace YemenDrive.Database.Migrations;

[DbContext(typeof(YemenDriveDbContext))]
[Migration("20260913153000_DefaultServiceKindAndServiceFee")]
public partial class DefaultServiceKindAndServiceFee : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "IsDefault", table: "ServiceKinds", type: "bit", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<decimal>(name: "ServiceFee", table: "PricingRules", type: "decimal(18,2)", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "ServiceFee", table: "Rides", type: "decimal(18,2)", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "TotalAmount", table: "Rides", type: "decimal(18,2)", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_ServiceKinds_IsDefault", table: "ServiceKinds", column: "IsDefault", unique: true, filter: "[IsDefault] = 1 AND [IsActive] = 1");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_ServiceKinds_IsDefault", table: "ServiceKinds");
        migrationBuilder.DropColumn(name: "IsDefault", table: "ServiceKinds");
        migrationBuilder.DropColumn(name: "ServiceFee", table: "PricingRules");
        migrationBuilder.DropColumn(name: "ServiceFee", table: "Rides");
        migrationBuilder.DropColumn(name: "TotalAmount", table: "Rides");
    }
}
