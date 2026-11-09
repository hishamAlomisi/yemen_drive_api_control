using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations;

[DbContext(typeof(YemenDriveDbContext))]
[Migration("20261105203000_DriverControlledWaitingResume")]
public sealed class DriverControlledWaitingResume : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "ResumeRequestsJson", table: "RideWaitingSessions", type: "nvarchar(max)", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "ResumeRequestsJson", table: "RideWaitingSessions");
}
