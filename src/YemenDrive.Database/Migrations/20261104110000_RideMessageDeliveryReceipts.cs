using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations;

public partial class RideMessageDeliveryReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "DeliveredAtUtc", table: "CommunicationMessages", type: "datetime2", nullable: true);
        // A historical read receipt proves delivery; no other legacy messages
        // receive an invented delivered/read timestamp during this upgrade.
        migrationBuilder.Sql("UPDATE [CommunicationMessages] SET [DeliveredAtUtc] = [ReadAtUtc] WHERE [ReadAtUtc] IS NOT NULL;");
        migrationBuilder.CreateIndex(
            name: "IX_CommunicationMessages_RecipientId_ReadAtUtc_RideId",
            table: "CommunicationMessages", columns: ["RecipientId", "ReadAtUtc", "RideId"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_CommunicationMessages_RecipientId_ReadAtUtc_RideId", "CommunicationMessages");
        migrationBuilder.DropColumn("DeliveredAtUtc", "CommunicationMessages");
    }
}
