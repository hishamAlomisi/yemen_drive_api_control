using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class FcmPushNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DevicePushInstallations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FcmToken = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    LastRegisteredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePushInstallations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevicePushInstallations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPushOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NotificationId = table.Column<int>(type: "int", nullable: false),
                    RecipientsCapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPushOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationPushOutbox_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPushDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OutboxId = table.Column<int>(type: "int", nullable: false),
                    DevicePushInstallationId = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AbandonedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPushDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationPushDeliveries_DevicePushInstallations_DevicePushInstallationId",
                        column: x => x.DevicePushInstallationId,
                        principalTable: "DevicePushInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationPushDeliveries_NotificationPushOutbox_OutboxId",
                        column: x => x.OutboxId,
                        principalTable: "NotificationPushOutbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevicePushInstallations_TokenHash",
                table: "DevicePushInstallations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevicePushInstallations_UserId_DeviceId",
                table: "DevicePushInstallations",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPushDeliveries_DevicePushInstallationId",
                table: "NotificationPushDeliveries",
                column: "DevicePushInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPushDeliveries_OutboxId_DevicePushInstallationId",
                table: "NotificationPushDeliveries",
                columns: new[] { "OutboxId", "DevicePushInstallationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPushDeliveries_SentAtUtc_AbandonedAtUtc_NextAttemptAtUtc_LeaseUntilUtc",
                table: "NotificationPushDeliveries",
                columns: new[] { "SentAtUtc", "AbandonedAtUtc", "NextAttemptAtUtc", "LeaseUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPushOutbox_NotificationId",
                table: "NotificationPushOutbox",
                column: "NotificationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPushOutbox_RecipientsCapturedAtUtc_CompletedAtUtc",
                table: "NotificationPushOutbox",
                columns: new[] { "RecipientsCapturedAtUtc", "CompletedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationPushDeliveries");

            migrationBuilder.DropTable(
                name: "DevicePushInstallations");

            migrationBuilder.DropTable(
                name: "NotificationPushOutbox");
        }
    }
}
