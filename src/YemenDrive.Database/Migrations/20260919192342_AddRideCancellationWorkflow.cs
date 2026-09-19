using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRideCancellationWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RideCancellationRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    RideId = table.Column<int>(type: "int", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    DriverId = table.Column<int>(type: "int", nullable: true),
                    RideStatusAtRequest = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DriverDecision = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RequestedRefundMethod = table.Column<int>(type: "int", nullable: true),
                    RequestedRefundAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CancellationLatitude = table.Column<double>(type: "float", nullable: true),
                    CancellationLongitude = table.Column<double>(type: "float", nullable: true),
                    LocationObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DriverDecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DriverNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AdminUserId = table.Column<int>(type: "int", nullable: true),
                    AdminDecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AdminNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RideCancellationRequests", x => x.Id);
                    table.CheckConstraint("CK_RideCancellationRequests_Reason_NotBlank", "LEN(LTRIM(RTRIM([Reason]))) >= 3");
                    table.ForeignKey(
                        name: "FK_RideCancellationRequests_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RideCancellationRequests_RideId_CreatedAtUtc",
                table: "RideCancellationRequests",
                columns: new[] { "RideId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RideCancellationRequests_RideId_Status",
                table: "RideCancellationRequests",
                columns: new[] { "RideId", "Status" },
                unique: true,
                filter: "[Status] IN (0, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RideCancellationRequests");
        }
    }
}
