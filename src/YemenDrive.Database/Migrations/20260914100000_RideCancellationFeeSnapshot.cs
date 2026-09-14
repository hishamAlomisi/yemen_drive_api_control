using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace YemenDrive.Database.Migrations;

[DbContext(typeof(YemenDriveDbContext))]
[Migration("20260914100000_RideCancellationFeeSnapshot")]
public partial class RideCancellationFeeSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(name: "CancellationFee", table: "Rides", type: "decimal(18,2)", nullable: false, defaultValue: 0m);
        migrationBuilder.AddCheckConstraint(name: "CK_Rides_CancellationFee_NonNegative", table: "Rides", sql: "[CancellationFee] >= 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_Rides_CancellationFee_NonNegative", table: "Rides");
        migrationBuilder.DropColumn(name: "CancellationFee", table: "Rides");
    }
}
