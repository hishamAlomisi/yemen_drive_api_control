using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class RideRouteChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LocationUpdates_RideId",
                table: "LocationUpdates");

            migrationBuilder.AddColumn<bool>(
                name: "HasRoutePricingSnapshot",
                table: "Rides",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "RouteBaseFare",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RouteChangeFee",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RouteChangeFees",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<double>(
                name: "RouteCompletedDistanceMeters",
                table: "Rides",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<decimal>(
                name: "RouteCompletedFare",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RouteOriginalFare",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RoutePerKilometer",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RoutePerMinute",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<double>(
                name: "RouteQuotedDistanceMeters",
                table: "Rides",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "RouteQuotedDurationSeconds",
                table: "Rides",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "RouteSegmentLatitude",
                table: "Rides",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "RouteSegmentLongitude",
                table: "Rides",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RouteSegmentStartedAtUtc",
                table: "Rides",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouteVersion",
                table: "Rides",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "BaseFare",
                table: "RidePricingQuotes",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<double>(
                name: "DistanceMeters",
                table: "RidePricingQuotes",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "DurationSeconds",
                table: "RidePricingQuotes",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "HasRoutePricingSnapshot",
                table: "RidePricingQuotes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PerKilometer",
                table: "RidePricingQuotes",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PerMinute",
                table: "RidePricingQuotes",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RouteChangeFee",
                table: "RidePricingQuotes",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RouteChangeFee",
                table: "PricingRules",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<double>(
                name: "Accuracy",
                table: "LocationUpdates",
                type: "float",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RideRouteChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RideId = table.Column<int>(type: "int", nullable: false),
                    RequestKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false),
                    RouteVersion = table.Column<int>(type: "int", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChangeObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangeLatitude = table.Column<double>(type: "float", nullable: false),
                    ChangeLongitude = table.Column<double>(type: "float", nullable: false),
                    OldDestinationLabel = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OldDestinationLatitude = table.Column<double>(type: "float", nullable: false),
                    OldDestinationLongitude = table.Column<double>(type: "float", nullable: false),
                    NewDestinationLabel = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    NewDestinationLatitude = table.Column<double>(type: "float", nullable: false),
                    NewDestinationLongitude = table.Column<double>(type: "float", nullable: false),
                    NewRoutePolyline = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreviousTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CompletedDistanceMeters = table.Column<double>(type: "float", nullable: false),
                    CompletedDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    CompletedFare = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OriginalServiceFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RouteChangeFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PreviousRouteChangeFees = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NewDistanceMeters = table.Column<double>(type: "float", nullable: false),
                    NewDurationSeconds = table.Column<double>(type: "float", nullable: false),
                    NewRouteFare = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WaitingFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NewTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RideRouteChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RideRouteChanges_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocationUpdates_RideId_ActorId_ObservedAtUtc",
                table: "LocationUpdates",
                columns: new[] { "RideId", "ActorId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RideRouteChanges_CreatedAtUtc_Id",
                table: "RideRouteChanges",
                columns: new[] { "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RideRouteChanges_RideId",
                table: "RideRouteChanges",
                column: "RideId",
                unique: true,
                filter: "[IsOpen] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RideRouteChanges_RideId_RequestKey",
                table: "RideRouteChanges",
                columns: new[] { "RideId", "RequestKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RideRouteChanges");

            migrationBuilder.DropIndex(
                name: "IX_LocationUpdates_RideId_ActorId_ObservedAtUtc",
                table: "LocationUpdates");

            migrationBuilder.DropColumn(
                name: "HasRoutePricingSnapshot",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteBaseFare",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteChangeFee",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteChangeFees",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteCompletedDistanceMeters",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteCompletedFare",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteOriginalFare",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RoutePerKilometer",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RoutePerMinute",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteQuotedDistanceMeters",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteQuotedDurationSeconds",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteSegmentLatitude",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteSegmentLongitude",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteSegmentStartedAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RouteVersion",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "BaseFare",
                table: "RidePricingQuotes");

            migrationBuilder.DropColumn(
                name: "DistanceMeters",
                table: "RidePricingQuotes");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "RidePricingQuotes");

            migrationBuilder.DropColumn(
                name: "HasRoutePricingSnapshot",
                table: "RidePricingQuotes");

            migrationBuilder.DropColumn(
                name: "PerKilometer",
                table: "RidePricingQuotes");

            migrationBuilder.DropColumn(
                name: "PerMinute",
                table: "RidePricingQuotes");

            migrationBuilder.DropColumn(
                name: "RouteChangeFee",
                table: "RidePricingQuotes");

            migrationBuilder.DropColumn(
                name: "RouteChangeFee",
                table: "PricingRules");

            migrationBuilder.DropColumn(
                name: "Accuracy",
                table: "LocationUpdates");

            migrationBuilder.CreateIndex(
                name: "IX_LocationUpdates_RideId",
                table: "LocationUpdates",
                column: "RideId");
        }
    }
}
