using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class ApprovedPickupAndReviewPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ArrivalToleranceMeters",
                table: "Rides",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DecisionResponseMinutes",
                table: "Rides",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DriverArrivedAtUtc",
                table: "Rides",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PickupGraceMinutes",
                table: "Rides",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RiderVerifiedPickupAtUtc",
                table: "Rides",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "RideFinancialCases",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                table: "RideFinancialCases",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "EvidenceReference",
                table: "RideFinancialCases",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedAdminId",
                table: "RideCancellationRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewDueAtUtc",
                table: "RideCancellationRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DriverEntitlementReversed",
                table: "PaymentTransactions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PhysicalCashReceived",
                table: "PaymentTransactions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OperationalPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArrivalToleranceMeters = table.Column<double>(type: "float", nullable: true),
                    PickupGraceMinutes = table.Column<int>(type: "int", nullable: true),
                    DecisionResponseMinutes = table.Column<int>(type: "int", nullable: true),
                    ApprovalBasis = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationalPolicies_Users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PickupContactAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RideId = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickupContactAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PickupContactAttempts_Rides_RideId",
                        column: x => x.RideId,
                        principalTable: "Rides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PickupContactAttempts_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RideFinancialCases_AssignedAdminId",
                table: "RideFinancialCases",
                column: "AssignedAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_RideCancellationRequests_AssignedAdminId",
                table: "RideCancellationRequests",
                column: "AssignedAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_RideCancellationRequests_Status_ReviewDueAtUtc",
                table: "RideCancellationRequests",
                columns: new[] { "Status", "ReviewDueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalPolicies_ApprovedByUserId",
                table: "OperationalPolicies",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PickupContactAttempts_ActorUserId",
                table: "PickupContactAttempts",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PickupContactAttempts_RideId_CreatedAtUtc",
                table: "PickupContactAttempts",
                columns: new[] { "RideId", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_RideCancellationRequests_Users_AssignedAdminId",
                table: "RideCancellationRequests",
                column: "AssignedAdminId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RideFinancialCases_Users_AssignedAdminId",
                table: "RideFinancialCases",
                column: "AssignedAdminId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RideCancellationRequests_Users_AssignedAdminId",
                table: "RideCancellationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_RideFinancialCases_Users_AssignedAdminId",
                table: "RideFinancialCases");

            migrationBuilder.DropTable(
                name: "OperationalPolicies");

            migrationBuilder.DropTable(
                name: "PickupContactAttempts");

            migrationBuilder.DropIndex(
                name: "IX_RideFinancialCases_AssignedAdminId",
                table: "RideFinancialCases");

            migrationBuilder.DropIndex(
                name: "IX_RideCancellationRequests_AssignedAdminId",
                table: "RideCancellationRequests");

            migrationBuilder.DropIndex(
                name: "IX_RideCancellationRequests_Status_ReviewDueAtUtc",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "ArrivalToleranceMeters",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "DecisionResponseMinutes",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "DriverArrivedAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "PickupGraceMinutes",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "RiderVerifiedPickupAtUtc",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "AssignedAdminId",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "ReviewDueAtUtc",
                table: "RideCancellationRequests");

            migrationBuilder.DropColumn(
                name: "DriverEntitlementReversed",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "PhysicalCashReceived",
                table: "PaymentTransactions");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "RideFinancialCases",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                table: "RideFinancialCases",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<string>(
                name: "EvidenceReference",
                table: "RideFinancialCases",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000,
                oldNullable: true);
        }
    }
}
