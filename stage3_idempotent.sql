IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [CommunicationMessages] (
        [Id] int NOT NULL IDENTITY,
        [RideId] int NOT NULL,
        [SenderId] int NOT NULL,
        [RecipientId] int NOT NULL,
        [MessageType] nvarchar(max) NOT NULL,
        [Content] nvarchar(max) NOT NULL,
        [ReadAtUtc] datetime2 NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_CommunicationMessages] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [DriverSettlements] (
        [Id] int NOT NULL IDENTITY,
        [DriverId] int NOT NULL,
        [PeriodStartUtc] datetime2 NOT NULL,
        [PeriodEndUtc] datetime2 NOT NULL,
        [GrossRideAmount] decimal(18,2) NOT NULL,
        [PlatformCommission] decimal(18,2) NOT NULL,
        [Adjustments] decimal(18,2) NOT NULL,
        [NetPayable] decimal(18,2) NOT NULL,
        [Status] int NOT NULL,
        [PaymentReference] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_DriverSettlements] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [EmergencyRecordings] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [RideId] int NULL,
        [StorageKey] nvarchar(max) NOT NULL,
        [ContentHash] nvarchar(max) NOT NULL,
        [DurationMilliseconds] bigint NOT NULL,
        [StartedAtUtc] datetime2 NOT NULL,
        [EndedAtUtc] datetime2 NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_EmergencyRecordings] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [JournalEntries] (
        [Id] int NOT NULL IDENTITY,
        [Reference] nvarchar(max) NOT NULL,
        [Description] nvarchar(max) NOT NULL,
        [PostedAtUtc] datetime2 NOT NULL,
        [IsPosted] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_JournalEntries] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [LedgerAccounts] (
        [Id] int NOT NULL IDENTITY,
        [Code] nvarchar(450) NOT NULL,
        [Name] nvarchar(max) NOT NULL,
        [Type] int NOT NULL,
        [Currency] nvarchar(max) NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_LedgerAccounts] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [Notifications] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Type] int NOT NULL,
        [Title] nvarchar(max) NOT NULL,
        [Body] nvarchar(max) NOT NULL,
        [DataJson] nvarchar(max) NULL,
        [IsRead] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_Notifications] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [PaymentCardTokens] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Provider] nvarchar(max) NOT NULL,
        [Token] nvarchar(max) NOT NULL,
        [LastFour] nvarchar(max) NOT NULL,
        [Brand] nvarchar(max) NOT NULL,
        [ExpiryMonth] int NOT NULL,
        [ExpiryYear] int NOT NULL,
        [IsDefault] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_PaymentCardTokens] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [PaymentTransactions] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [RideId] int NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Currency] nvarchar(max) NOT NULL,
        [Provider] nvarchar(max) NOT NULL,
        [Status] int NOT NULL,
        [ProviderReference] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_PaymentTransactions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [Promotions] (
        [Id] int NOT NULL IDENTITY,
        [Code] nvarchar(450) NOT NULL,
        [Name] nvarchar(max) NOT NULL,
        [FixedDiscount] decimal(18,2) NULL,
        [PercentageDiscount] decimal(18,2) NULL,
        [MaximumDiscount] decimal(18,2) NULL,
        [StartsAtUtc] datetime2 NOT NULL,
        [EndsAtUtc] datetime2 NOT NULL,
        [UsageLimit] int NULL,
        [UsageCount] int NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_Promotions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [ServiceKinds] (
        [Id] int NOT NULL IDENTITY,
        [Code] nvarchar(80) NOT NULL,
        [NameAr] nvarchar(200) NOT NULL,
        [ImageUrl] nvarchar(500) NULL,
        [IsActive] bit NOT NULL,
        [SortOrder] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_ServiceKinds] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] int NOT NULL IDENTITY,
        [PhoneNumber] nvarchar(30) NOT NULL,
        [DisplayName] nvarchar(150) NULL,
        [Email] nvarchar(254) NULL,
        [Gender] nvarchar(32) NULL,
        [Street] nvarchar(200) NULL,
        [City] nvarchar(100) NULL,
        [District] nvarchar(100) NULL,
        [PasswordHash] nvarchar(300) NOT NULL,
        [Role] int NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [JournalLines] (
        [Id] int NOT NULL IDENTITY,
        [JournalEntryId] int NOT NULL,
        [LedgerAccountId] int NOT NULL,
        [Debit] decimal(18,2) NOT NULL,
        [Credit] decimal(18,2) NOT NULL,
        [UserId] int NULL,
        [RideId] int NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_JournalLines] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_JournalLines_JournalEntries_JournalEntryId] FOREIGN KEY ([JournalEntryId]) REFERENCES [JournalEntries] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_JournalLines_LedgerAccounts_LedgerAccountId] FOREIGN KEY ([LedgerAccountId]) REFERENCES [LedgerAccounts] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [RideServiceCatalogItems] (
        [Id] int NOT NULL IDENTITY,
        [Code] nvarchar(80) NOT NULL,
        [NameAr] nvarchar(200) NOT NULL,
        [ServiceKindId] int NOT NULL,
        [ArrivalMinutes] int NOT NULL,
        [BasePrice] decimal(18,2) NOT NULL,
        [Rating] decimal(18,2) NOT NULL,
        [Seats] int NOT NULL,
        [DescriptionAr] nvarchar(1000) NOT NULL,
        [ImageUrl] nvarchar(500) NULL,
        [IsRecommended] bit NOT NULL,
        [IsActive] bit NOT NULL,
        [SortOrder] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_RideServiceCatalogItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RideServiceCatalogItems_ServiceKinds_ServiceKindId] FOREIGN KEY ([ServiceKindId]) REFERENCES [ServiceKinds] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [DriverLiveLocations] (
        [Id] int NOT NULL IDENTITY,
        [DriverId] int NOT NULL,
        [Latitude] float NOT NULL,
        [Longitude] float NOT NULL,
        [Bearing] float NULL,
        [Speed] float NULL,
        [IsOnline] bit NOT NULL,
        [ObservedAtUtc] datetime2 NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_DriverLiveLocations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DriverLiveLocations_Users_DriverId] FOREIGN KEY ([DriverId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [ReferralRedemptions] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Code] nvarchar(100) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_ReferralRedemptions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ReferralRedemptions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [SavedPlaces] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Label] nvarchar(450) NOT NULL,
        [Kind] nvarchar(32) NOT NULL,
        [Address] nvarchar(max) NOT NULL,
        [Latitude] float NOT NULL,
        [Longitude] float NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_SavedPlaces] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SavedPlaces_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
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
        CONSTRAINT [FK_SupportTickets_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [Wallets] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Balance] decimal(18,2) NOT NULL,
        [Currency] nvarchar(max) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_Wallets] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Wallets_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [DriverProfiles] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [ServiceKindId] int NOT NULL,
        [ServiceCatalogItemId] int NOT NULL,
        [VehicleModel] nvarchar(max) NOT NULL,
        [PlateNumber] nvarchar(max) NOT NULL,
        [CommissionRate] decimal(18,2) NOT NULL,
        [IsAvailable] bit NOT NULL,
        [Rating] decimal(18,2) NOT NULL,
        [PhotoUrl] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_DriverProfiles] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DriverProfiles_RideServiceCatalogItems_ServiceCatalogItemId] FOREIGN KEY ([ServiceCatalogItemId]) REFERENCES [RideServiceCatalogItems] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DriverProfiles_ServiceKinds_ServiceKindId] FOREIGN KEY ([ServiceKindId]) REFERENCES [ServiceKinds] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DriverProfiles_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [PricingRules] (
        [Id] int NOT NULL IDENTITY,
        [ServiceKindId] int NOT NULL,
        [ServiceCatalogItemId] int NOT NULL,
        [BaseFare] decimal(18,2) NOT NULL,
        [PerKilometer] decimal(18,2) NOT NULL,
        [PerMinute] decimal(18,2) NOT NULL,
        [DriverShareRate] decimal(18,2) NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_PricingRules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PricingRules_RideServiceCatalogItems_ServiceCatalogItemId] FOREIGN KEY ([ServiceCatalogItemId]) REFERENCES [RideServiceCatalogItems] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PricingRules_ServiceKinds_ServiceKindId] FOREIGN KEY ([ServiceKindId]) REFERENCES [ServiceKinds] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [Rides] (
        [Id] int NOT NULL IDENTITY,
        [RowVersion] rowversion NOT NULL,
        [CustomerId] int NOT NULL,
        [DriverId] int NULL,
        [Status] int NOT NULL,
        [ServiceKindId] int NOT NULL,
        [ServiceCatalogItemId] int NOT NULL,
        [PickupLabel] nvarchar(max) NOT NULL,
        [PickupAddress] nvarchar(max) NOT NULL,
        [PickupLatitude] float NOT NULL,
        [PickupLongitude] float NOT NULL,
        [DestinationLabel] nvarchar(max) NOT NULL,
        [DestinationAddress] nvarchar(max) NOT NULL,
        [DestinationLatitude] float NOT NULL,
        [DestinationLongitude] float NOT NULL,
        [ServerPrice] decimal(18,2) NULL,
        [CustomerPrice] decimal(18,2) NULL,
        [DriverShare] decimal(18,2) NULL,
        [PlatformShare] decimal(18,2) NULL,
        [RoutePolyline] nvarchar(max) NULL,
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_Rides] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Rides_RideServiceCatalogItems_ServiceCatalogItemId] FOREIGN KEY ([ServiceCatalogItemId]) REFERENCES [RideServiceCatalogItems] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Rides_ServiceKinds_ServiceKindId] FOREIGN KEY ([ServiceKindId]) REFERENCES [ServiceKinds] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Rides_Users_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Rides_Users_DriverId] FOREIGN KEY ([DriverId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [WalletTransactions] (
        [Id] int NOT NULL IDENTITY,
        [WalletId] int NOT NULL,
        [RideId] int NULL,
        [Type] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [BalanceAfter] decimal(18,2) NOT NULL,
        [Description] nvarchar(max) NOT NULL,
        [ExternalReference] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_WalletTransactions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_WalletTransactions_Wallets_WalletId] FOREIGN KEY ([WalletId]) REFERENCES [Wallets] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [LocationUpdates] (
        [Id] int NOT NULL IDENTITY,
        [RideId] int NOT NULL,
        [ActorId] int NOT NULL,
        [Latitude] float NOT NULL,
        [Longitude] float NOT NULL,
        [Bearing] float NULL,
        [Speed] float NULL,
        [ObservedAtUtc] datetime2 NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_LocationUpdates] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_LocationUpdates_Rides_RideId] FOREIGN KEY ([RideId]) REFERENCES [Rides] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE TABLE [RideOffers] (
        [Id] int NOT NULL IDENTITY,
        [RideId] int NOT NULL,
        [DriverId] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Status] int NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [Note] nvarchar(max) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_RideOffers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RideOffers_Rides_RideId] FOREIGN KEY ([RideId]) REFERENCES [Rides] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_RideOffers_Users_DriverId] FOREIGN KEY ([DriverId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DriverLiveLocations_DriverId] ON [DriverLiveLocations] ([DriverId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_DriverProfiles_ServiceCatalogItemId] ON [DriverProfiles] ([ServiceCatalogItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_DriverProfiles_ServiceKindId_ServiceCatalogItemId] ON [DriverProfiles] ([ServiceKindId], [ServiceCatalogItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DriverProfiles_UserId] ON [DriverProfiles] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_JournalLines_JournalEntryId] ON [JournalLines] ([JournalEntryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_JournalLines_LedgerAccountId] ON [JournalLines] ([LedgerAccountId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_LedgerAccounts_Code] ON [LedgerAccounts] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_LocationUpdates_RideId] ON [LocationUpdates] ([RideId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_PricingRules_ServiceCatalogItemId] ON [PricingRules] ([ServiceCatalogItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_PricingRules_ServiceKindId_ServiceCatalogItemId_IsActive] ON [PricingRules] ([ServiceKindId], [ServiceCatalogItemId], [IsActive]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Promotions_Code] ON [Promotions] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_ReferralRedemptions_UserId_Code] ON [ReferralRedemptions] ([UserId], [Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_RideOffers_DriverId] ON [RideOffers] ([DriverId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_RideOffers_RideId] ON [RideOffers] ([RideId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_Rides_CustomerId] ON [Rides] ([CustomerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_Rides_DriverId] ON [Rides] ([DriverId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_Rides_ServiceCatalogItemId] ON [Rides] ([ServiceCatalogItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_Rides_ServiceKindId] ON [Rides] ([ServiceKindId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RideServiceCatalogItems_Code] ON [RideServiceCatalogItems] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_RideServiceCatalogItems_ServiceKindId_IsActive_SortOrder] ON [RideServiceCatalogItems] ([ServiceKindId], [IsActive], [SortOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_SavedPlaces_UserId_Label] ON [SavedPlaces] ([UserId], [Label]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ServiceKinds_Code] ON [ServiceKinds] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_ServiceKinds_IsActive_SortOrder] ON [ServiceKinds] ([IsActive], [SortOrder]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_SupportTickets_UserId_CreatedAtUtc] ON [SupportTickets] ([UserId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_PhoneNumber] ON [Users] ([PhoneNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Wallets_UserId] ON [Wallets] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    CREATE INDEX [IX_WalletTransactions_WalletId] ON [WalletTransactions] ([WalletId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912183308_Stage3SchemaBaseline'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260912183308_Stage3SchemaBaseline', N'9.0.9');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    DROP INDEX [IX_WalletTransactions_WalletId] ON [WalletTransactions];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    DROP INDEX [IX_RideOffers_RideId] ON [RideOffers];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    DECLARE @var sysname;
    SELECT @var = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[RideOffers]') AND [c].[name] = N'Note');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [RideOffers] DROP CONSTRAINT [' + @var + '];');
    ALTER TABLE [RideOffers] ALTER COLUMN [Note] nvarchar(1000) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    DECLARE @var1 sysname;
    SELECT @var1 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[PaymentTransactions]') AND [c].[name] = N'ProviderReference');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [PaymentTransactions] DROP CONSTRAINT [' + @var1 + '];');
    ALTER TABLE [PaymentTransactions] ALTER COLUMN [ProviderReference] nvarchar(450) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    DECLARE @var2 sysname;
    SELECT @var2 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[CommunicationMessages]') AND [c].[name] = N'MessageType');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [CommunicationMessages] DROP CONSTRAINT [' + @var2 + '];');
    ALTER TABLE [CommunicationMessages] ALTER COLUMN [MessageType] nvarchar(32) NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    DECLARE @var3 sysname;
    SELECT @var3 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[CommunicationMessages]') AND [c].[name] = N'Content');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [CommunicationMessages] DROP CONSTRAINT [' + @var3 + '];');
    ALTER TABLE [CommunicationMessages] ALTER COLUMN [Content] nvarchar(4000) NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    CREATE INDEX [IX_WalletTransactions_WalletId_CreatedAtUtc] ON [WalletTransactions] ([WalletId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    CREATE INDEX [IX_Rides_Status_ServiceKindId_ServiceCatalogItemId_CreatedAtUtc] ON [Rides] ([Status], [ServiceKindId], [ServiceCatalogItemId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_RideOffers_RideId_DriverId_Status] ON [RideOffers] ([RideId], [DriverId], [Status]) WHERE [Status] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_PaymentTransactions_ProviderReference] ON [PaymentTransactions] ([ProviderReference]) WHERE [ProviderReference] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    CREATE INDEX [IX_PaymentTransactions_UserId_RideId_CreatedAtUtc] ON [PaymentTransactions] ([UserId], [RideId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    CREATE INDEX [IX_Notifications_UserId_IsRead_CreatedAtUtc] ON [Notifications] ([UserId], [IsRead], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    CREATE INDEX [IX_CommunicationMessages_RideId_CreatedAtUtc] ON [CommunicationMessages] ([RideId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912184039_Stage3IntegrityIndexes'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260912184039_Stage3IntegrityIndexes', N'9.0.9');
END;

COMMIT;
GO

