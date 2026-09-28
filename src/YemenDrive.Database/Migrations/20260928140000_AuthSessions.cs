using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using YemenDrive.Database;

#nullable disable

namespace YemenDrive.Database.Migrations;

[DbContext(typeof(YemenDriveDbContext))]
[Migration("20260928140000_AuthSessions")]
public partial class AuthSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuthSessions",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<int>(type: "int", nullable: false),
                AccessTokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                RefreshTokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                AccessExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                SessionExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                RotatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_AuthSessions_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuthSessions_AccessTokenHash",
            table: "AuthSessions",
            column: "AccessTokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AuthSessions_FamilyId",
            table: "AuthSessions",
            column: "FamilyId");

        migrationBuilder.CreateIndex(
            name: "IX_AuthSessions_RefreshTokenHash",
            table: "AuthSessions",
            column: "RefreshTokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AuthSessions_UserId_SessionExpiresAtUtc",
            table: "AuthSessions",
            columns: new[] { "UserId", "SessionExpiresAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("AuthSessions");

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "9.0.9")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);
        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);
        modelBuilder.Entity("YemenDrive.Database.Entities.AuthSession", entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("int");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<int>("Id"));
            entity.Property<DateTime>("AccessExpiresAtUtc").HasColumnType("datetime2");
            entity.Property<string>("AccessTokenHash").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("datetime2");
            entity.Property<string>("DeviceId").HasMaxLength(128).HasColumnType("nvarchar(128)");
            entity.Property<Guid>("FamilyId").HasColumnType("uniqueidentifier");
            entity.Property<DateTime?>("RevokedAtUtc").HasColumnType("datetime2");
            entity.Property<byte[]>("RowVersion").IsConcurrencyToken().IsRequired().ValueGeneratedOnAddOrUpdate().HasColumnType("rowversion");
            entity.Property<DateTime>("SessionExpiresAtUtc").HasColumnType("datetime2");
            entity.Property<DateTime?>("RotatedAtUtc").HasColumnType("datetime2");
            entity.Property<string>("RefreshTokenHash").IsRequired().HasMaxLength(64).HasColumnType("nvarchar(64)");
            entity.Property<DateTime?>("UpdatedAtUtc").HasColumnType("datetime2");
            entity.Property<int>("UserId").HasColumnType("int");
            entity.HasKey("Id");
            entity.HasIndex("AccessTokenHash").IsUnique();
            entity.HasIndex("FamilyId");
            entity.HasIndex("RefreshTokenHash").IsUnique();
            entity.HasIndex("UserId", "SessionExpiresAtUtc");
            entity.ToTable("AuthSessions");
        });
        modelBuilder.Entity("YemenDrive.Database.Entities.User", entity =>
        {
            entity.Property<int>("Id").HasColumnType("int");
            entity.HasKey("Id");
            entity.ToTable("Users");
        });
        modelBuilder.Entity("YemenDrive.Database.Entities.AuthSession", entity =>
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
