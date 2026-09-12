using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class Stage3FinancialIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some development databases were created with EnsureCreated before
            // migration history existed.  Guard each DDL operation so adopting
            // those schemas is safe while a fresh database receives all rules.
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'[Wallets]', N'RowVersion') IS NULL
                    ALTER TABLE [Wallets] ADD [RowVersion] rowversion NOT NULL;

                IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_WalletTransactions_Amount_Positive')
                    ALTER TABLE [WalletTransactions] ADD CONSTRAINT [CK_WalletTransactions_Amount_Positive] CHECK ([Amount] > 0);

                IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Wallets_Balance_NonNegative')
                    ALTER TABLE [Wallets] ADD CONSTRAINT [CK_Wallets_Balance_NonNegative] CHECK ([Balance] >= 0);

                IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Rides_CustomerPrice_NonNegative')
                    ALTER TABLE [Rides] ADD CONSTRAINT [CK_Rides_CustomerPrice_NonNegative] CHECK ([CustomerPrice] IS NULL OR [CustomerPrice] >= 0);

                IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Rides_ServerPrice_NonNegative')
                    ALTER TABLE [Rides] ADD CONSTRAINT [CK_Rides_ServerPrice_NonNegative] CHECK ([ServerPrice] IS NULL OR [ServerPrice] >= 0);

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_PaymentTransactions_RideId' AND [object_id] = OBJECT_ID(N'[PaymentTransactions]'))
                    CREATE UNIQUE INDEX [IX_PaymentTransactions_RideId] ON [PaymentTransactions] ([RideId]) WHERE [RideId] IS NOT NULL AND [Status] = 2;

                IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_PaymentTransactions_Amount_Positive')
                    ALTER TABLE [PaymentTransactions] ADD CONSTRAINT [CK_PaymentTransactions_Amount_Positive] CHECK ([Amount] > 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WalletTransactions_Amount_Positive",
                table: "WalletTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Wallets_Balance_NonNegative",
                table: "Wallets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Rides_CustomerPrice_NonNegative",
                table: "Rides");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Rides_ServerPrice_NonNegative",
                table: "Rides");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_RideId",
                table: "PaymentTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentTransactions_Amount_Positive",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Wallets");
        }
    }
}
