using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class WaitingAndNegotiationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AppliedDriverCommissionFixed",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AppliedDriverCommissionRate",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxCustomerPrice",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinCustomerPrice",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PricingRuleId",
                table: "Rides",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuotedPrice",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WaitingFee",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "WaitingFreeMinutes",
                table: "Rides",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "WaitingMaxFee",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "WaitingPricePerMinute",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxPriceDecreasePercent",
                table: "PricingRules",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxPriceIncreasePercent",
                table: "PricingRules",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "WaitingFreeMinutes",
                table: "PricingRules",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "WaitingMaxFee",
                table: "PricingRules",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "WaitingPricePerMinute",
                table: "PricingRules",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "RidePricingQuotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Token = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    ServiceKindId = table.Column<int>(type: "int", nullable: false),
                    ServiceCatalogItemId = table.Column<int>(type: "int", nullable: false),
                    PricingRuleId = table.Column<int>(type: "int", nullable: false),
                    PickupLatitude = table.Column<double>(type: "float", nullable: false),
                    PickupLongitude = table.Column<double>(type: "float", nullable: false),
                    DestinationLatitude = table.Column<double>(type: "float", nullable: false),
                    DestinationLongitude = table.Column<double>(type: "float", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MinPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ServiceFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CancellationFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DriverCommissionRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DriverCommissionFixed = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WaitingFreeMinutes = table.Column<int>(type: "int", nullable: false),
                    WaitingPricePerMinute = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WaitingMaxFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RidePricingQuotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RideWaitingSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RideId = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    RequestKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    FreeMinutes = table.Column<int>(type: "int", nullable: false),
                    PricePerMinute = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    MotionStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastMotionObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    GraceWarningSent = table.Column<bool>(type: "bit", nullable: false),
                    BillingNoticeSent = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RideWaitingSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RideWaitingSessions_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RidePricingQuotes_ExpiresAtUtc",
                table: "RidePricingQuotes",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RidePricingQuotes_Token",
                table: "RidePricingQuotes",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RideWaitingSessions_RideId",
                table: "RideWaitingSessions",
                column: "RideId",
                unique: true,
                filter: "[IsOpen] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RideWaitingSessions_RideId_RequestKey",
                table: "RideWaitingSessions",
                columns: new[] { "RideId", "RequestKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RidePricingQuotes");

            migrationBuilder.DropTable(
                name: "RideWaitingSessions");

            migrationBuilder.DropColumn(
                name: "AppliedDriverCommissionFixed",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "AppliedDriverCommissionRate",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "MaxCustomerPrice",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "MinCustomerPrice",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "PricingRuleId",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "QuotedPrice",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "WaitingFee",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "WaitingFreeMinutes",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "WaitingMaxFee",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "WaitingPricePerMinute",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "MaxPriceDecreasePercent",
                table: "PricingRules");

            migrationBuilder.DropColumn(
                name: "MaxPriceIncreasePercent",
                table: "PricingRules");

            migrationBuilder.DropColumn(
                name: "WaitingFreeMinutes",
                table: "PricingRules");

            migrationBuilder.DropColumn(
                name: "WaitingMaxFee",
                table: "PricingRules");

            migrationBuilder.DropColumn(
                name: "WaitingPricePerMinute",
                table: "PricingRules");
        }
    }
}
