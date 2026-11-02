using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class SavedPlaceNamedDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedPlaces_UserId_LocationKey",
                table: "SavedPlaces");

            migrationBuilder.CreateIndex(
                name: "IX_SavedPlaces_UserId_LocationKey",
                table: "SavedPlaces",
                columns: new[] { "UserId", "LocationKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedPlaces_UserId_LocationKey",
                table: "SavedPlaces");

            migrationBuilder.CreateIndex(
                name: "IX_SavedPlaces_UserId_LocationKey",
                table: "SavedPlaces",
                columns: new[] { "UserId", "LocationKey" },
                unique: true);
        }
    }
}
