using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class Stage3IntegrityIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletTransactions_WalletId",
                table: "WalletTransactions");

            migrationBuilder.DropIndex(
                name: "IX_RideOffers_RideId",
                table: "RideOffers");

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "RideOffers",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProviderReference",
                table: "PaymentTransactions",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "MessageType",
                table: "CommunicationMessages",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Content",
                table: "CommunicationMessages",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_WalletId_CreatedAtUtc",
                table: "WalletTransactions",
                columns: new[] { "WalletId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Rides_Status_ServiceKindId_ServiceCatalogItemId_CreatedAtUtc",
                table: "Rides",
                columns: new[] { "Status", "ServiceKindId", "ServiceCatalogItemId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RideOffers_RideId_DriverId_Status",
                table: "RideOffers",
                columns: new[] { "RideId", "DriverId", "Status" },
                unique: true,
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_ProviderReference",
                table: "PaymentTransactions",
                column: "ProviderReference",
                unique: true,
                filter: "[ProviderReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_UserId_RideId_CreatedAtUtc",
                table: "PaymentTransactions",
                columns: new[] { "UserId", "RideId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_IsRead_CreatedAtUtc",
                table: "Notifications",
                columns: new[] { "UserId", "IsRead", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationMessages_RideId_CreatedAtUtc",
                table: "CommunicationMessages",
                columns: new[] { "RideId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletTransactions_WalletId_CreatedAtUtc",
                table: "WalletTransactions");

            migrationBuilder.DropIndex(
                name: "IX_Rides_Status_ServiceKindId_ServiceCatalogItemId_CreatedAtUtc",
                table: "Rides");

            migrationBuilder.DropIndex(
                name: "IX_RideOffers_RideId_DriverId_Status",
                table: "RideOffers");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_ProviderReference",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_UserId_RideId_CreatedAtUtc",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_UserId_IsRead_CreatedAtUtc",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_CommunicationMessages_RideId_CreatedAtUtc",
                table: "CommunicationMessages");

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "RideOffers",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProviderReference",
                table: "PaymentTransactions",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "MessageType",
                table: "CommunicationMessages",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "Content",
                table: "CommunicationMessages",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000);

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_WalletId",
                table: "WalletTransactions",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_RideOffers_RideId",
                table: "RideOffers",
                column: "RideId");
        }
    }
}
