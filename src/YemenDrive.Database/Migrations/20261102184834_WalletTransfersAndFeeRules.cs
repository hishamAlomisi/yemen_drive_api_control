using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class WalletTransfersAndFeeRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TransferLockedUntilUtc",
                table: "Wallets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TransferPasswordFailures",
                table: "Wallets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "WalletTransferFeeRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FromAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ToAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    FeeKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FeeValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransferFeeRules", x => x.Id);
                    table.CheckConstraint("CK_WalletTransferFeeRule_Range", "[FromAmount] >= 0 AND ([ToAmount] IS NULL OR [ToAmount] > [FromAmount]) AND [FeeValue] >= 0 AND [FeeKind] IN ('Fixed','Percent')");
                });

            migrationBuilder.CreateTable(
                name: "WalletTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Reference = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderUserId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    RecipientName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    RecipientPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RecipientRole = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FeeRuleId = table.Column<int>(type: "int", nullable: false),
                    FeeKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FeeValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SenderBalanceAfter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    RecipientBalanceAfter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    JournalEntryId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransfers", x => x.Id);
                    table.CheckConstraint("CK_WalletTransfer_Amounts", "[Amount] > 0 AND [Fee] >= 0 AND [SenderUserId] <> [RecipientUserId]");
                    table.ForeignKey(
                        name: "FK_WalletTransfers_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletTransfers_Users_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletTransfers_WalletTransferFeeRules_FeeRuleId",
                        column: x => x.FeeRuleId,
                        principalTable: "WalletTransferFeeRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_FeeRuleId",
                table: "WalletTransfers",
                column: "FeeRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_RecipientUserId",
                table: "WalletTransfers",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_Reference",
                table: "WalletTransfers",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransfers_SenderUserId_RequestId",
                table: "WalletTransfers",
                columns: new[] { "SenderUserId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletTransfers");

            migrationBuilder.DropTable(
                name: "WalletTransferFeeRules");

            migrationBuilder.DropColumn(
                name: "TransferLockedUntilUtc",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "TransferPasswordFailures",
                table: "Wallets");
        }
    }
}
