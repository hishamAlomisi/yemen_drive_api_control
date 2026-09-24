using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedPlaceLocationUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "LocationKey", table: "SavedPlaces", type: "nvarchar(48)", maxLength: 48, nullable: true);
            migrationBuilder.Sql("UPDATE [SavedPlaces] SET [LocationKey] = FORMAT([Latitude], 'F4', 'en-US') + '|' + FORMAT([Longitude], 'F4', 'en-US');");
            migrationBuilder.Sql("WITH d AS (SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [UserId], [LocationKey] ORDER BY CASE WHEN [Kind] = 'recent' THEN 1 ELSE 0 END, [UpdatedAtUtc] DESC, [CreatedAtUtc] DESC, [Id] DESC) AS n FROM [SavedPlaces]) DELETE FROM [SavedPlaces] WHERE [Id] IN (SELECT [Id] FROM d WHERE n > 1);");
            migrationBuilder.AlterColumn<string>(name: "LocationKey", table: "SavedPlaces", type: "nvarchar(48)", maxLength: 48, nullable: false, oldClrType: typeof(string), oldType: "nvarchar(48)", oldMaxLength: 48, oldNullable: true);
            migrationBuilder.CreateIndex(name: "IX_SavedPlaces_UserId_LocationKey", table: "SavedPlaces", columns: new[] { "UserId", "LocationKey" }, unique: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_SavedPlaces_UserId_LocationKey", table: "SavedPlaces");
            migrationBuilder.DropColumn(name: "LocationKey", table: "SavedPlaces");

        }
    }
}
