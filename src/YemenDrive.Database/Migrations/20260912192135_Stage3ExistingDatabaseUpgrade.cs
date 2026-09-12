using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YemenDrive.Database.Migrations
{
    /// <inheritdoc />
    public partial class Stage3ExistingDatabaseUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[ReferralRedemptions]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [ReferralRedemptions] (
                        [Id] int NOT NULL IDENTITY,
                        [UserId] int NOT NULL,
                        [Code] nvarchar(100) NOT NULL,
                        [Status] nvarchar(32) NOT NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        [UpdatedAtUtc] datetime2 NULL,
                        CONSTRAINT [PK_ReferralRedemptions] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_ReferralRedemptions_Users_UserId]
                            FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
                    );
                    CREATE INDEX [IX_ReferralRedemptions_UserId_Code]
                        ON [ReferralRedemptions] ([UserId], [Code]);
                END;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[SupportTickets]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [SupportTickets] (
                        [Id] int NOT NULL IDENTITY,
                        [UserId] int NOT NULL,
                        [Category] nvarchar(100) NOT NULL,
                        [Message] nvarchar(2000) NOT NULL,
                        [Status] nvarchar(32) NOT NULL,
                        [AdminReply] nvarchar(2000) NULL,
                        [ResolvedAtUtc] datetime2 NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        [UpdatedAtUtc] datetime2 NULL,
                        CONSTRAINT [PK_SupportTickets] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_SupportTickets_Users_UserId]
                            FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
                    );
                    CREATE INDEX [IX_SupportTickets_UserId_CreatedAtUtc]
                        ON [SupportTickets] ([UserId], [CreatedAtUtc]);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally non-destructive: this migration may upgrade an existing database.
        }
    }
}
