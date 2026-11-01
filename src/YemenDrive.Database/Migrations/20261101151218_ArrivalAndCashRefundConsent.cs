using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class ArrivalAndCashRefundConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RideCancellationRequests_RideId_Status",
                table: "RideCancellationRequests");

            migrationBuilder.AddColumn<Guid>(
                name: "ArrivalAlertId",
                table: "Rides",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArrivalAlertSentAtUtc",
                table: "Rides",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArrivalAlertStoppedAtUtc",
                table: "Rides",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DriverArrivalAcknowledgedAtUtc",
                table: "Rides",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RefundChoiceRevision",
                table: "RideCancellationRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundConsentedAtUtc",
                table: "RideCancellationRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequestedByDriver",
                table: "RideCancellationRequests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_RideCancellationRequests_RideId_Status",
                table: "RideCancellationRequests",
                columns: new[] { "RideId", "Status" },
                unique: true,
                filter: "[Status] IN (0, 2, 5, 9, 10)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RideCancellationRequests_RideId_Status",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "ArrivalAlertId",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "ArrivalAlertSentAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "ArrivalAlertStoppedAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "DriverArrivalAcknowledgedAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RefundChoiceRevision",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "RefundConsentedAtUtc",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "RequestedByDriver",
                table: "RideCancellationRequests");

            migrationBuilder.CreateIndex(
                name: "IX_RideCancellationRequests_RideId_Status",
                table: "RideCancellationRequests",
                columns: new[] { "RideId", "Status" },
                unique: true,
                filter: "[Status] IN (0, 2)");
        }
    }
}
