using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class ManualSmsSending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SmsMessageLogs_Purpose",
                table: "SmsMessageLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SmsDispatches_Purpose",
                table: "SmsDispatches");

            migrationBuilder.AddColumn<int>(
                name: "RecipientCount",
                table: "SmsMessageLogs",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "SmsDispatches",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestKey",
                table: "SmsDispatches",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SmsMessageLogs_Purpose",
                table: "SmsMessageLogs",
                sql: "[Purpose] IN (1, 2, 3, 4, 5, 6)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SmsMessageLogs_Recipients",
                table: "SmsMessageLogs",
                sql: "[RecipientCount] BETWEEN 1 AND 100");

            migrationBuilder.CreateIndex(
                name: "IX_SmsDispatches_RequestKey",
                table: "SmsDispatches",
                column: "RequestKey",
                unique: true,
                filter: "[RequestKey] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SmsDispatches_Purpose",
                table: "SmsDispatches",
                sql: "[Purpose] IN (1, 2, 3, 4, 5, 6)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SmsMessageLogs_Purpose",
                table: "SmsMessageLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SmsMessageLogs_Recipients",
                table: "SmsMessageLogs");

            migrationBuilder.DropIndex(
                name: "IX_SmsDispatches_RequestKey",
                table: "SmsDispatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SmsDispatches_Purpose",
                table: "SmsDispatches");

            migrationBuilder.DropColumn(
                name: "RecipientCount",
                table: "SmsMessageLogs");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "SmsDispatches");

            migrationBuilder.DropColumn(
                name: "RequestKey",
                table: "SmsDispatches");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SmsMessageLogs_Purpose",
                table: "SmsMessageLogs",
                sql: "[Purpose] IN (1, 2, 3, 4, 5)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SmsDispatches_Purpose",
                table: "SmsDispatches",
                sql: "[Purpose] IN (1, 2, 3, 4, 5)");
        }
    }
}
