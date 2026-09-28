using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using YemenDrive.Database;

#nullable disable

namespace YemenDrive.Database.Migrations;

[DbContext(typeof(YemenDriveDbContext))]
[Migration("20260928120000_TrustedDevices")]
public partial class TrustedDevices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TrustedDevices",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<int>(type: "int", nullable: false),
                DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                LastUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TrustedDevices", x => x.Id);
                table.ForeignKey(
                    name: "FK_TrustedDevices_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TrustedDevices_TokenHash",
            table: "TrustedDevices",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TrustedDevices_UserId_DeviceId",
            table: "TrustedDevices",
            columns: new[] { "UserId", "DeviceId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "TrustedDevices");

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "9.0.9")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);
        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);
        modelBuilder.Entity("YemenDrive.Database.Entities.TrustedDevice", entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("int");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<int>("Id"));
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("datetime2");
            entity.Property<string>("DeviceId").IsRequired().HasMaxLength(128).HasColumnType("nvarchar(128)");
            entity.Property<DateTime>("ExpiresAtUtc").HasColumnType("datetime2");
            entity.Property<DateTime?>("LastUsedAtUtc").HasColumnType("datetime2");
            entity.Property<DateTime?>("RevokedAtUtc").HasColumnType("datetime2");
            entity.Property<string>("TokenHash").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            entity.Property<DateTime?>("UpdatedAtUtc").HasColumnType("datetime2");
            entity.Property<int>("UserId").HasColumnType("int");
            entity.HasKey("Id");
            entity.HasIndex("TokenHash").IsUnique();
            entity.HasIndex("UserId", "DeviceId").IsUnique();
            entity.ToTable("TrustedDevices");
        });
        modelBuilder.Entity("YemenDrive.Database.Entities.User", entity =>
        {
            entity.Property<int>("Id").HasColumnType("int");
            entity.HasKey("Id");
            entity.ToTable("Users");
        });
        modelBuilder.Entity("YemenDrive.Database.Entities.TrustedDevice", entity =>
        {
            entity.HasOne("YemenDrive.Database.Entities.User", "User")
                .WithMany()
                .HasForeignKey("UserId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.Navigation("User");
        });
#pragma warning restore 612, 618
    }
}
