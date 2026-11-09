using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class DurableRealtimeStateEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RealtimeStateEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StreamKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Resource = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResourceId = table.Column<int>(type: "int", nullable: false),
                    Changes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeStateEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeStateStreams",
                columns: table => new
                {
                    StreamKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeStateStreams", x => x.StreamKey);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeStateRecipients",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeStateRecipients", x => new { x.EventId, x.UserId });
                    table.ForeignKey(
                        name: "FK_RealtimeStateRecipients_RealtimeStateEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "RealtimeStateEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeStateEvents_CreatedAtUtc",
                table: "RealtimeStateEvents",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeStateEvents_PublishedAtUtc_LeaseUntilUtc_CreatedAtUtc",
                table: "RealtimeStateEvents",
                columns: new[] { "PublishedAtUtc", "LeaseUntilUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeStateEvents_StreamKey_Version",
                table: "RealtimeStateEvents",
                columns: new[] { "StreamKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeStateRecipients_UserId_EventId",
                table: "RealtimeStateRecipients",
                columns: new[] { "UserId", "EventId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RealtimeStateRecipients");

            migrationBuilder.DropTable(
                name: "RealtimeStateStreams");

            migrationBuilder.DropTable(
                name: "RealtimeStateEvents");
        }
    }
}
