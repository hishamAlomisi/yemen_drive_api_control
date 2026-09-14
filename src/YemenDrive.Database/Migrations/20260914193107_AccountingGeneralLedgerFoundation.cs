using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class AccountingGeneralLedgerFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_JournalEntries_JournalEntryId",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_LedgerAccounts_LedgerAccountId",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalLines_JournalEntryId",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalLines_LedgerAccountId",
                table: "JournalLines");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "LedgerAccounts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Currency",
                table: "LedgerAccounts",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "LedgerAccounts",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AddColumn<int>(
                name: "FinancialPartyId",
                table: "LedgerAccounts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPosting",
                table: "LedgerAccounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "LedgerAccounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ParentLedgerAccountId",
                table: "LedgerAccounts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Purpose",
                table: "LedgerAccounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "JournalLines",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "JournalLines",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FinancialPartyId",
                table: "JournalLines",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LineNumber",
                table: "JournalLines",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "Reference",
                table: "JournalEntries",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "JournalEntries",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "JournalEntries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "JournalEntries",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EntryNumber",
                table: "JournalEntries",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "JournalEntries",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OccurredAtUtc",
                table: "JournalEntries",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "PostedByUserId",
                table: "JournalEntries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversesJournalEntryId",
                table: "JournalEntries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "JournalEntries",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "JournalEntries",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "JournalEntries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalCredit",
                table: "JournalEntries",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalDebit",
                table: "JournalEntries",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "JournalEntries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "FinancialParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    EntityId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialParties", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_FinancialPartyId_Purpose_Currency",
                table: "LedgerAccounts",
                columns: new[] { "FinancialPartyId", "Purpose", "Currency" },
                unique: true,
                filter: "[FinancialPartyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_ParentLedgerAccountId",
                table: "LedgerAccounts",
                column: "ParentLedgerAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_FinancialPartyId",
                table: "JournalLines",
                column: "FinancialPartyId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_JournalEntryId_LineNumber",
                table: "JournalLines",
                columns: new[] { "JournalEntryId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_LedgerAccountId_CreatedAtUtc",
                table: "JournalLines",
                columns: new[] { "LedgerAccountId", "CreatedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_JournalLines_ExactlyOneSide",
                table: "JournalLines",
                sql: "([Debit] > 0 AND [Credit] = 0) OR ([Credit] > 0 AND [Debit] = 0)");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_EntryNumber",
                table: "JournalEntries",
                column: "EntryNumber",
                unique: true,
                filter: "[EntryNumber] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ReversesJournalEntryId",
                table: "JournalEntries",
                column: "ReversesJournalEntryId",
                unique: true,
                filter: "[ReversesJournalEntryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_SourceType_SourceId_Type",
                table: "JournalEntries",
                columns: new[] { "SourceType", "SourceId", "Type" },
                unique: true,
                filter: "[SourceType] IS NOT NULL AND [SourceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_Status_PostedAtUtc",
                table: "JournalEntries",
                columns: new[] { "Status", "PostedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_JournalEntries_Totals_Balanced",
                table: "JournalEntries",
                sql: "([Status] = 0 AND [TotalDebit] = 0 AND [TotalCredit] = 0) OR ([Status] = 1 AND [TotalDebit] = [TotalCredit] AND [TotalDebit] > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialParties_Code",
                table: "FinancialParties",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialParties_Type_EntityId",
                table: "FinancialParties",
                columns: new[] { "Type", "EntityId" },
                unique: true,
                filter: "[EntityId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntries_JournalEntries_ReversesJournalEntryId",
                table: "JournalEntries",
                column: "ReversesJournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_FinancialParties_FinancialPartyId",
                table: "JournalLines",
                column: "FinancialPartyId",
                principalTable: "FinancialParties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_JournalEntries_JournalEntryId",
                table: "JournalLines",
                column: "JournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_LedgerAccounts_LedgerAccountId",
                table: "JournalLines",
                column: "LedgerAccountId",
                principalTable: "LedgerAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerAccounts_FinancialParties_FinancialPartyId",
                table: "LedgerAccounts",
                column: "FinancialPartyId",
                principalTable: "FinancialParties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerAccounts_LedgerAccounts_ParentLedgerAccountId",
                table: "LedgerAccounts",
                column: "ParentLedgerAccountId",
                principalTable: "LedgerAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntries_JournalEntries_ReversesJournalEntryId",
                table: "JournalEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_FinancialParties_FinancialPartyId",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_JournalEntries_JournalEntryId",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_LedgerAccounts_LedgerAccountId",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerAccounts_FinancialParties_FinancialPartyId",
                table: "LedgerAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerAccounts_LedgerAccounts_ParentLedgerAccountId",
                table: "LedgerAccounts");

            migrationBuilder.DropTable(
                name: "FinancialParties");

            migrationBuilder.DropIndex(
                name: "IX_LedgerAccounts_FinancialPartyId_Purpose_Currency",
                table: "LedgerAccounts");

            migrationBuilder.DropIndex(
                name: "IX_LedgerAccounts_ParentLedgerAccountId",
                table: "LedgerAccounts");

            migrationBuilder.DropIndex(
                name: "IX_JournalLines_FinancialPartyId",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalLines_JournalEntryId_LineNumber",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalLines_LedgerAccountId_CreatedAtUtc",
                table: "JournalLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JournalLines_ExactlyOneSide",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_EntryNumber",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_ReversesJournalEntryId",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_SourceType_SourceId_Type",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_Status_PostedAtUtc",
                table: "JournalEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JournalEntries_Totals_Balanced",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "FinancialPartyId",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "IsPosting",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "ParentLedgerAccountId",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "JournalLines");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "JournalLines");

            migrationBuilder.DropColumn(
                name: "FinancialPartyId",
                table: "JournalLines");

            migrationBuilder.DropColumn(
                name: "LineNumber",
                table: "JournalLines");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "EntryNumber",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "OccurredAtUtc",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "PostedByUserId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "ReversesJournalEntryId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "TotalCredit",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "TotalDebit",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "JournalEntries");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "LedgerAccounts",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Currency",
                table: "LedgerAccounts",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(12)",
                oldMaxLength: 12);

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "LedgerAccounts",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(80)",
                oldMaxLength: 80);

            migrationBuilder.AlterColumn<string>(
                name: "Reference",
                table: "JournalEntries",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(160)",
                oldMaxLength: 160);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "JournalEntries",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000);

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_JournalEntryId",
                table: "JournalLines",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_LedgerAccountId",
                table: "JournalLines",
                column: "LedgerAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_JournalEntries_JournalEntryId",
                table: "JournalLines",
                column: "JournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_LedgerAccounts_LedgerAccountId",
                table: "JournalLines",
                column: "LedgerAccountId",
                principalTable: "LedgerAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
