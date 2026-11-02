using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class VerifiedAlternativeRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RefundFundingLedgerAccountId",
                table: "RideCancellationRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefundPayoutEvidenceReference",
                table: "RideCancellationRequests",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TravelledFareSnapshot",
                table: "RideCancellationRequests",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RideCancellationRequests_RefundFundingLedgerAccountId",
                table: "RideCancellationRequests",
                column: "RefundFundingLedgerAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_RideCancellationRequests_LedgerAccounts_RefundFundingLedgerAccountId",
                table: "RideCancellationRequests",
                column: "RefundFundingLedgerAccountId",
                principalTable: "LedgerAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RideCancellationRequests_LedgerAccounts_RefundFundingLedgerAccountId",
                table: "RideCancellationRequests");

            migrationBuilder.DropIndex(
                name: "IX_RideCancellationRequests_RefundFundingLedgerAccountId",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "RefundFundingLedgerAccountId",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "RefundPayoutEvidenceReference",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "TravelledFareSnapshot",
                table: "RideCancellationRequests");
        }
    }
}
