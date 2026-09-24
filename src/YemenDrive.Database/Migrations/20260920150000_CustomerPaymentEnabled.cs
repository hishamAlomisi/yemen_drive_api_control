using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace YemenDrive.Database.Migrations;

[Migration("20260920150000_CustomerPaymentEnabled")]
[DbContext(typeof(YemenDriveDbContext))]
public partial class CustomerPaymentEnabled : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(
            name: "CustomerPaymentEnabled",
            table: "Rides",
            type: "bit",
            nullable: false,
            defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "CustomerPaymentEnabled", table: "Rides");
}
