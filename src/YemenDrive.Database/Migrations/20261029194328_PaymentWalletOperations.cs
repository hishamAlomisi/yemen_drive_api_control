using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class PaymentWalletOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentWalletOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RideId = table.Column<int>(type: "int", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    PaymentMethodId = table.Column<int>(type: "int", nullable: false),
                    ProviderCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RequestId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProviderOperationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RuntimeInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NextAction = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Fees = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    FeesKnown = table.Column<bool>(type: "bit", nullable: false),
                    FeeCurrency = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    Mobile = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProviderSucceededAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaymentTransactionId = table.Column<int>(type: "int", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentWalletOperations", x => x.Id);
                    table.CheckConstraint("CK_PaymentWalletOperations_Amounts", "[Amount] > 0 AND ([Fees] IS NULL OR [Fees] >= 0) AND [Currency] = 'YER' AND [FeeCurrency] = 'YER'");
                    table.ForeignKey(
                        name: "FK_PaymentWalletOperations_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentWalletOperations_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWalletOperations_CustomerId_RideId_CreatedAtUtc",
                table: "PaymentWalletOperations",
                columns: new[] { "CustomerId", "RideId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWalletOperations_PaymentMethodId",
                table: "PaymentWalletOperations",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWalletOperations_RequestId",
                table: "PaymentWalletOperations",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWalletOperations_RideId",
                table: "PaymentWalletOperations",
                column: "RideId",
                unique: true,
                filter: "[IsOpen] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentWalletOperations");
        }
    }
}
