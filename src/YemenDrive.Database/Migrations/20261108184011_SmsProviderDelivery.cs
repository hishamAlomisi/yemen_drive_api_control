using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class SmsProviderDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SmsMessageLogs_Purpose",
                table: "SmsMessageLogs");

            migrationBuilder.CreateTable(
                name: "SmsDispatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1000000000000, 1"),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WalletTransactionId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsDispatches", x => x.Id);
                    table.CheckConstraint("CK_SmsDispatches_Purpose", "[Purpose] IN (1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_SmsDispatches_State", "[State] IN (0, 1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_SmsDispatches_WalletTransactions_WalletTransactionId",
                        column: x => x.WalletTransactionId,
                        principalTable: "WalletTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SmsMessageLogs_Purpose",
                table: "SmsMessageLogs",
                sql: "[Purpose] IN (1, 2, 3, 4, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_SmsDispatches_State_CreatedAtUtc",
                table: "SmsDispatches",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SmsDispatches_WalletTransactionId",
                table: "SmsDispatches",
                column: "WalletTransactionId",
                unique: true,
                filter: "[WalletTransactionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmsDispatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SmsMessageLogs_Purpose",
                table: "SmsMessageLogs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SmsMessageLogs_Purpose",
                table: "SmsMessageLogs",
                sql: "[Purpose] IN (1, 2, 3)");
        }
    }
}
