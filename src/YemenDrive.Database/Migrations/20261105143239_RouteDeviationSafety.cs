using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class RouteDeviationSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviationDirection",
                table: "SafetyIncidents",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeviationDirectionDegrees",
                table: "SafetyIncidents",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeviationMeters",
                table: "SafetyIncidents",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "SafetyIncidents",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LocationAccuracyMeters",
                table: "SafetyIncidents",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationSource",
                table: "SafetyIncidents",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouteVersion",
                table: "SafetyIncidents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedRoutePolyline",
                table: "SafetyIncidents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoutePolyline",
                table: "RidePricingQuotes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SafetyIncidents_RideId_Kind_RouteVersion_CreatedAtUtc",
                table: "SafetyIncidents",
                columns: new[] { "RideId", "Kind", "RouteVersion", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SafetyIncidents_RideId_Kind_RouteVersion_CreatedAtUtc",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "DeviationDirection",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "DeviationDirectionDegrees",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "DeviationMeters",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "LocationAccuracyMeters",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "LocationSource",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "RouteVersion",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "VerifiedRoutePolyline",
                table: "SafetyIncidents");

            migrationBuilder.DropColumn(
                name: "RoutePolyline",
                table: "RidePricingQuotes");
        }
    }
}
