using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class ApprovedWalletLedgerDebt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LedgerDebt",
                table: "Wallets",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "DebtApprovalReason",
                table: "WalletLedgerApplications",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DebtApprovedByUserId",
                table: "WalletLedgerApplications",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Wallets_LedgerDebt",
                table: "Wallets",
                sql: "[LedgerDebt] >= 0 AND ([LedgerDebt] = 0 OR [Balance] = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Wallets_LedgerDebt",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "LedgerDebt",
                table: "Wallets");

            migrationBuilder.DropColumn(
                name: "DebtApprovalReason",
                table: "WalletLedgerApplications");

            migrationBuilder.DropColumn(
                name: "DebtApprovedByUserId",
                table: "WalletLedgerApplications");
        }
    }
}
