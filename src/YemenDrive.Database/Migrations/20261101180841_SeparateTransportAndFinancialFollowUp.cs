using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class SeparateTransportAndFinancialFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_RideId",
                table: "PaymentTransactions");

            migrationBuilder.AlterColumn<string>(
                name: "Message",
                table: "SupportTickets",
                type: "nvarchar(max)",
                maxLength: 10000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AlterColumn<string>(
                name: "AdminReply",
                table: "SupportTickets",
                type: "nvarchar(max)",
                maxLength: 10000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedAdminId",
                table: "SupportTickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastDecisionByUserId",
                table: "SupportTickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RideId",
                table: "SupportTickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CollectedDriverEntitlement",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FinancialAmountDue",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FinancialDriverEntitlement",
                table: "Rides",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PassengerBoardedAtUtc",
                table: "Rides",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PassengerCount",
                table: "Rides",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceEndReason",
                table: "Rides",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ServiceEndedAtUtc",
                table: "Rides",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CashReturnedAtUtc",
                table: "RideCancellationRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerCashReceiptConfirmed",
                table: "RideCancellationRequests",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefundDestination",
                table: "RideCancellationRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Responsibility",
                table: "RideCancellationRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaiveCancellationFees",
                table: "RideCancellationRequests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "DriverNetAmount",
                table: "PaymentTransactions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSupplemental",
                table: "PaymentTransactions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ServiceFeeAmount",
                table: "PaymentTransactions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AcceptsNewRequests",
                table: "DriverProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedPassengerSeats",
                table: "DriverProfiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsApproved",
                table: "DriverProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AccountDeletionRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AdminNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecidedByUserId = table.Column<int>(type: "int", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountDeletionRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdministratorPermissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdministratorPermissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationalDecisionAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubjectId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalDecisionAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RideFinancialCases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RideId = table.Column<int>(type: "int", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    DriverId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceReference = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    AssignedAdminId = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EvidenceReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecidedByUserId = table.Column<int>(type: "int", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AccruedReceivable = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AccruedServiceFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AccruedDriverEntitlement = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JournalRevision = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RideFinancialCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RideFinancialCases_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupportTicketEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SupportTicketId = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTicketEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupportTicketEntries_SupportTickets_SupportTicketId",
                        column: x => x.SupportTicketId,
                        principalTable: "SupportTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_RideId",
                table: "PaymentTransactions",
                column: "RideId",
                unique: true,
                filter: "[RideId] IS NOT NULL AND [Status] = 2 AND [IsSupplemental] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_AccountDeletionRequests_UserId",
                table: "AccountDeletionRequests",
                column: "UserId",
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_AdministratorPermissions_UserId_Permission",
                table: "AdministratorPermissions",
                columns: new[] { "UserId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RideFinancialCases_RideId",
                table: "RideFinancialCases",
                column: "RideId");

            migrationBuilder.CreateIndex(
                name: "IX_RideFinancialCases_SourceReference",
                table: "RideFinancialCases",
                column: "SourceReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketEntries_SupportTicketId",
                table: "SupportTicketEntries",
                column: "SupportTicketId");

            // Preserve existing administrator authority; subsequent administrator
            // accounts receive only explicit grants. No financial history is rewritten.
            migrationBuilder.Sql("""
                UPDATE o SET IsOpen = 0
                FROM PaymentWalletOperations o
                INNER JOIN PaymentTransactions p ON p.Id = o.PaymentTransactionId
                WHERE o.Status = N'Succeeded' AND p.Status = 2 AND o.IsOpen = 1;
                INSERT INTO AdministratorPermissions (UserId, Permission, CreatedAtUtc)
                SELECT Id, '*', GETUTCDATE() FROM Users WHERE Role = 2 AND IsActive = 1;
                INSERT INTO OperationalDecisionAudits (ActorUserId, Subject, SubjectId, Action, Reason, CreatedAtUtc)
                SELECT Id, 'AdministratorPermission', Id, 'legacy_grant', N'حفظ تفويض حساب الإدارة الموجود عند الترحيل', GETUTCDATE()
                FROM Users WHERE Role = 2 AND IsActive = 1;
                UPDATE DriverProfiles SET AcceptsNewRequests = 1, IsApproved = IsAvailable;
                UPDATE Rides SET ServiceEndedAtUtc = COALESCE(CompletedAtUtc, UpdatedAtUtc, CreatedAtUtc),
                    ServiceEndReason = 'historical_terminal_state'
                WHERE Status IN (6,7);
                UPDATE r SET FinancialAmountDue = CASE WHEN COALESCE(p.NetPaid,0) > 0 THEN p.NetPaid ELSE 0 END
                FROM Rides r OUTER APPLY
                    (SELECT SUM(CASE WHEN Status = 2 THEN Amount WHEN Status = 4 THEN -Amount ELSE 0 END) NetPaid
                     FROM PaymentTransactions WHERE RideId = r.Id) p WHERE r.Status = 7;
                UPDATE r SET CollectedDriverEntitlement = CASE WHEN COALESCE(e.Amount,0) > 0 THEN e.Amount ELSE 0 END
                FROM Rides r OUTER APPLY
                    (SELECT SUM(l.Debit-l.Credit) Amount FROM JournalLines l
                     JOIN LedgerAccounts a ON a.Id = l.LedgerAccountId
                     WHERE l.RideId = r.Id AND a.Code = '5100') e
                WHERE EXISTS (SELECT 1 FROM PaymentTransactions p WHERE p.RideId = r.Id AND p.Status = 2)
                  AND EXISTS (SELECT 1 FROM JournalLines l JOIN LedgerAccounts a ON a.Id = l.LedgerAccountId WHERE l.RideId = r.Id AND a.Code = '5100');
                UPDATE Rides SET FinancialDriverEntitlement = COALESCE(CollectedDriverEntitlement,0) WHERE Status = 7;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountDeletionRequests");

            migrationBuilder.DropTable(
                name: "AdministratorPermissions");

            migrationBuilder.DropTable(
                name: "OperationalDecisionAudits");

            migrationBuilder.DropTable(
                name: "RideFinancialCases");

            migrationBuilder.DropTable(
                name: "SupportTicketEntries");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_RideId",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "AssignedAdminId",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "LastDecisionByUserId",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "RideId",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "CollectedDriverEntitlement",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "FinancialAmountDue",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "FinancialDriverEntitlement",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "PassengerBoardedAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "PassengerCount",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "ServiceEndReason",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "ServiceEndedAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "CashReturnedAtUtc",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "CustomerCashReceiptConfirmed",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "RefundDestination",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "Responsibility",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "WaiveCancellationFees",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "DriverNetAmount",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "IsSupplemental",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "ServiceFeeAmount",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "AcceptsNewRequests",
                table: "DriverProfiles");

            migrationBuilder.DropColumn(
                name: "ApprovedPassengerSeats",
                table: "DriverProfiles");

            migrationBuilder.DropColumn(
                name: "IsApproved",
                table: "DriverProfiles");

            migrationBuilder.AlterColumn<string>(
                name: "Message",
                table: "SupportTickets",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldMaxLength: 10000);

            migrationBuilder.AlterColumn<string>(
                name: "AdminReply",
                table: "SupportTickets",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldMaxLength: 10000,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_RideId",
                table: "PaymentTransactions",
                column: "RideId",
                unique: true,
                filter: "[RideId] IS NOT NULL AND [Status] = 2");
        }
    }
}
