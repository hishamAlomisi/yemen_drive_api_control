BEGIN TRANSACTION;
ALTER TABLE [Wallets] ADD [RowVersion] rowversion NOT NULL;

ALTER TABLE [WalletTransactions] ADD CONSTRAINT [CK_WalletTransactions_Amount_Positive] CHECK ([Amount] > 0);

ALTER TABLE [Wallets] ADD CONSTRAINT [CK_Wallets_Balance_NonNegative] CHECK ([Balance] >= 0);

ALTER TABLE [Rides] ADD CONSTRAINT [CK_Rides_CustomerPrice_NonNegative] CHECK ([CustomerPrice] IS NULL OR [CustomerPrice] >= 0);

ALTER TABLE [Rides] ADD CONSTRAINT [CK_Rides_ServerPrice_NonNegative] CHECK ([ServerPrice] IS NULL OR [ServerPrice] >= 0);

CREATE UNIQUE INDEX [IX_PaymentTransactions_RideId] ON [PaymentTransactions] ([RideId]) WHERE [RideId] IS NOT NULL AND [Status] = 2;

ALTER TABLE [PaymentTransactions] ADD CONSTRAINT [CK_PaymentTransactions_Amount_Positive] CHECK ([Amount] > 0);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260912193419_Stage3FinancialIntegrity', N'9.0.9');

COMMIT;
GO

