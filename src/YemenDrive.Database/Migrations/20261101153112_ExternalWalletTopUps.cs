using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class ExternalWalletTopUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentWalletOperations_RideId",
                table: "PaymentWalletOperations");

            migrationBuilder.AlterColumn<int>(
                name: "RideId",
                table: "PaymentWalletOperations",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<decimal>(
                name: "BalanceAfter",
                table: "PaymentWalletOperations",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JournalEntryId",
                table: "PaymentWalletOperations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalletTransactionId",
                table: "PaymentWalletOperations",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWalletOperations_CustomerId",
                table: "PaymentWalletOperations",
                column: "CustomerId",
                unique: true,
                filter: "[IsOpen] = 1 AND [RideId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWalletOperations_RideId",
                table: "PaymentWalletOperations",
                column: "RideId",
                unique: true,
                filter: "[IsOpen] = 1 AND [RideId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentWalletOperations_CustomerId",
                table: "PaymentWalletOperations");

            migrationBuilder.DropIndex(
                name: "IX_PaymentWalletOperations_RideId",
                table: "PaymentWalletOperations");

            migrationBuilder.DropColumn(
                name: "BalanceAfter",
                table: "PaymentWalletOperations");

            migrationBuilder.DropColumn(
                name: "JournalEntryId",
                table: "PaymentWalletOperations");

            migrationBuilder.DropColumn(
                name: "WalletTransactionId",
                table: "PaymentWalletOperations");

            migrationBuilder.AlterColumn<int>(
                name: "RideId",
                table: "PaymentWalletOperations",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWalletOperations_RideId",
                table: "PaymentWalletOperations",
                column: "RideId",
                unique: true,
                filter: "[IsOpen] = 1");
        }
    }
}
