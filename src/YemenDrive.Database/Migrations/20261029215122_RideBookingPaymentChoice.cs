using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class RideBookingPaymentChoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentMethodCode",
                table: "Rides",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "cash");

            migrationBuilder.AddColumn<bool>(
                name: "PaymentRequiredBeforeStart",
                table: "Rides",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentMethodCode",
                table: "Rides");

            migrationBuilder.DropColumn(
                name: "PaymentRequiredBeforeStart",
                table: "Rides");
        }
    }
}
