using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class SafetyRecordingStreaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "StorageKey",
                table: "EmergencyRecordings",
                type: "nvarchar(180)",
                maxLength: 180,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "ContentHash",
                table: "EmergencyRecordings",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentAtUtc",
                table: "EmergencyRecordings",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChunkAtUtc",
                table: "EmergencyRecordings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UploadedBytes",
                table: "EmergencyRecordings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyRecordings_StorageKey",
                table: "EmergencyRecordings",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyRecordings_UserId_RideId_EndedAtUtc",
                table: "EmergencyRecordings",
                columns: new[] { "UserId", "RideId", "EndedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmergencyRecordings_StorageKey",
                table: "EmergencyRecordings");

            migrationBuilder.DropIndex(
                name: "IX_EmergencyRecordings_UserId_RideId_EndedAtUtc",
                table: "EmergencyRecordings");

            migrationBuilder.DropColumn(
                name: "ConsentAtUtc",
                table: "EmergencyRecordings");

            migrationBuilder.DropColumn(
                name: "LastChunkAtUtc",
                table: "EmergencyRecordings");

            migrationBuilder.DropColumn(
                name: "UploadedBytes",
                table: "EmergencyRecordings");

            migrationBuilder.AlterColumn<string>(
                name: "StorageKey",
                table: "EmergencyRecordings",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(180)",
                oldMaxLength: 180);

            migrationBuilder.AlterColumn<string>(
                name: "ContentHash",
                table: "EmergencyRecordings",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);
        }
    }
}
