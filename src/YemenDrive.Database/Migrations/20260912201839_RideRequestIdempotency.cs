using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class RideRequestIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rides_CustomerId",
                table: "Rides");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "Rides",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rides_CustomerId_IdempotencyKey",
                table: "Rides",
                columns: new[] { "CustomerId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rides_CustomerId_IdempotencyKey",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "Rides");

            migrationBuilder.CreateIndex(
                name: "IX_Rides_CustomerId",
                table: "Rides",
                column: "CustomerId");
        }
    }
}
