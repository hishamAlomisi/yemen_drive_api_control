namespace YemenDrive.Database.Entities;

public sealed class WalletTransferFeeRule : Entity
{
    public decimal FromAmount { get; set; }
    public decimal? ToAmount { get; set; }
    public string FeeKind { get; set; } = "Fixed";
    public decimal FeeValue { get; set; }
    public bool IsActive { get; set; } = true;
    public int UpdatedByUserId { get; set; }
}

public sealed class WalletTransfer : Entity
{
    public Guid Reference { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public int SenderUserId { get; set; }
    public int RecipientUserId { get; set; }
    public string RecipientName { get; set; } = "";
    public string RecipientPhone { get; set; } = "";
    public UserRole RecipientRole { get; set; }
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public string Currency { get; set; } = "YER";
    public string Note { get; set; } = "";
    public int FeeRuleId { get; set; }
    public string FeeKind { get; set; } = "";
    public decimal FeeValue { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public decimal? SenderBalanceAfter { get; set; }
    public decimal? RecipientBalanceAfter { get; set; }
    public int? JournalEntryId { get; set; }
}
