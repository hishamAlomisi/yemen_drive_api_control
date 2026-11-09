using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class DriverMovementHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rides_DriverId",
                table: "Rides");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "LocationUpdates",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RideMovementSnapshots",
                columns: table => new
                {
                    RideId = table.Column<int>(type: "int", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PickupLatitude = table.Column<double>(type: "float", nullable: false),
                    PickupLongitude = table.Column<double>(type: "float", nullable: false),
                    PickupLabel = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DestinationLatitude = table.Column<double>(type: "float", nullable: false),
                    DestinationLongitude = table.Column<double>(type: "float", nullable: false),
                    DestinationLabel = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PlannedRoutePolyline = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PlannedDurationSeconds = table.Column<double>(type: "float", nullable: true),
                    PassengerLatitude = table.Column<double>(type: "float", nullable: true),
                    PassengerLongitude = table.Column<double>(type: "float", nullable: true),
                    PassengerAccuracy = table.Column<double>(type: "float", nullable: true),
                    PassengerObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RideMovementSnapshots", x => x.RideId);
                    table.ForeignKey(
                        name: "FK_RideMovementSnapshots_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RideRequestDriverLocations",
                columns: table => new
                {
                    RideId = table.Column<int>(type: "int", nullable: false),
                    DriverId = table.Column<int>(type: "int", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RideRequestDriverLocations", x => new { x.RideId, x.DriverId });
                    table.ForeignKey(
                        name: "FK_RideRequestDriverLocations_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RideRequestDriverLocations_Users_DriverId",
                        column: x => x.DriverId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Rides_DriverId_CreatedAtUtc",
                table: "Rides",
                columns: new[] { "DriverId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RideRequestDriverLocations_DriverId",
                table: "RideRequestDriverLocations",
                column: "DriverId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RideMovementSnapshots");

            migrationBuilder.DropTable(
                name: "RideRequestDriverLocations");

            migrationBuilder.DropIndex(
                name: "IX_Rides_DriverId_CreatedAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "LocationUpdates");

            migrationBuilder.CreateIndex(
                name: "IX_Rides_DriverId",
                table: "Rides",
                column: "DriverId");
        }
    }
}
