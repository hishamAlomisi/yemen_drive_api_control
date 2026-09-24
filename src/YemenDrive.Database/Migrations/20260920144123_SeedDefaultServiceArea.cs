using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class SeedDefaultServiceArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ServiceAreas",
                columns: new[] { "CountryCode", "CountryNameAr", "CityNameAr", "IsActive", "CreatedAtUtc" },
                values: new object[] { "YE", "اليمن", "صنعاء", true, new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc) });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "ServiceAreas", keyColumn: "CountryCode", keyValue: "YE");

        }
    }
}
