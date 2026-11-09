using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class SmsMessageStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SmsMessageLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsMessageLogs", x => x.Id);
                    table.CheckConstraint("CK_SmsMessageLogs_Purpose", "[Purpose] IN (1, 2, 3)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SmsMessageLogs_MessageKey",
                table: "SmsMessageLogs",
                column: "MessageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SmsMessageLogs_Purpose_SentAtUtc",
                table: "SmsMessageLogs",
                columns: new[] { "Purpose", "SentAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmsMessageLogs");
        }
    }
}
