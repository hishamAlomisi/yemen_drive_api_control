namespace YemenDrive.Database.Entities;

/// <summary>Idempotent evidence that a journal's wallet effect reached the operational balance.</summary>
public sealed class WalletLedgerApplication : Entity
{
    public int JournalEntryId { get; set; }
    public JournalEntry JournalEntry { get; set; } = null!;
    public int WalletId { get; set; }
    public Wallet Wallet { get; set; } = null!;
    public decimal Delta { get; set; }
    public int? DebtApprovedByUserId { get; set; }
    public string? DebtApprovalReason { get; set; }
}
