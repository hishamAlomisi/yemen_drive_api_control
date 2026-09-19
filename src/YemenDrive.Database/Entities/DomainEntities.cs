namespace YemenDrive.Database.Entities;

public enum UserRole { Customer, Driver, Admin }
public enum RideStatus { Draft, Searching, Negotiating, DriverAssigned, DriverEnRoute, InProgress, Completed, Cancelled, CancellationPending }
public enum OfferStatus { Pending, Accepted, Rejected, Expired }
public enum WalletTransactionType { Credit, Debit, Hold, Release, Refund, Commission }
public enum NotificationType { RideOffer, RideStatus, Payment, Safety, System }
public enum LedgerAccountType { Asset, Liability, Equity, Revenue, Expense }
public enum FinancialPartyType
{
    System = 0, User = 1, Wallet = 2, ExternalProvider = 3,
    CashBox = 4, Bank = 5, Other = 6, ServiceKind = 7
}
public enum LedgerAccountPurpose
{
    General, CustomerWallet, DriverCurrentAccount, ServiceFee,
    ServiceCollection, DriverCommission, ExternalWallet, CashCollection
}
public enum JournalEntryType
{
    OpeningBalance, RideCashCollection, RideWalletPayment, WalletTopUp,
    RideCancellation, DriverSettlementCollection, ManualAdjustment, Reversal
}
public enum JournalEntryStatus { Draft, Posted }
public enum CashCollectionApprovalStatus { Pending, Approved, Rejected, InsufficientBalance }
/// <summary>Customer-selected cash payment awaits the driver's confirmation before collection.</summary>
public enum CashPaymentRequestStatus { Pending, DriverConfirmed, DriverRejected, Collected }
/// <summary>
/// The customer's declared resolution for a paid cash ride cancellation.
/// Values are explicit because this value is sent by the mobile client.
/// </summary>
public enum CashCancellationRefundMethod { ReturnFromDriver = 1, CreditCustomerWallet = 2 }
/// <summary>Lifecycle of a cancellation case.  Financial reversal is only possible after administration approves it.</summary>
public enum RideCancellationStatus { DriverReviewPending, DriverRejected, AdminReviewPending, AdminApproved, AdminRejected }
public enum RideCancellationDriverDecision { None, Accepted, Rejected, ReferredToAdmin }
public enum PaymentStatus { Pending, Authorized, Paid, Failed, Refunded, Cancelled }
public enum PaymentMethodKind { ExternalWallet, Card, BankTransfer }

public abstract class Entity
{
    public int Id { get; set; } = 0;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class User : Entity
{
    public required string PhoneNumber { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Gender { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Customer;
    public bool IsActive { get; set; } = true;
    public DriverProfile? DriverProfile { get; set; }
    public Wallet? Wallet { get; set; }
}

public sealed class DriverProfile : Entity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int ServiceKindId { get; set; }
    public ServiceKind ServiceKind { get; set; } = null!;
    public int ServiceCatalogItemId { get; set; }
    public RideServiceCatalogItem ServiceCatalogItem { get; set; } = null!;
    public string VehicleModel { get; set; } = string.Empty;
    public string PlateNumber { get; set; } = string.Empty;
    public decimal CommissionRate { get; set; }
    public bool IsAvailable { get; set; }
    public decimal Rating { get; set; }
    public string? PhotoUrl { get; set; }
}

public sealed class DriverLiveLocation : Entity
{
    public int DriverId { get; set; }
    public User Driver { get; set; } = null!;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Bearing { get; set; }
    public double? Speed { get; set; }
    public bool IsOnline { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SavedPlace : Entity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Label { get; set; } = string.Empty;
    public string Kind { get; set; } = "place";
    public string Address { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed class Ride : Entity
{
    public byte[] RowVersion { get; set; } = [];
    public string? IdempotencyKey { get; set; }
    public int CustomerId { get; set; }
    public User Customer { get; set; } = null!;
    public int? DriverId { get; set; }
    public User? Driver { get; set; }
    public RideStatus Status { get; set; } = RideStatus.Draft;
    public int ServiceKindId { get; set; }
    public ServiceKind ServiceKind { get; set; } = null!;
    public int ServiceCatalogItemId { get; set; }
    public RideServiceCatalogItem ServiceCatalogItem { get; set; } = null!;
    public string PickupLabel { get; set; } = string.Empty;
    public string PickupAddress { get; set; } = string.Empty;
    public double PickupLatitude { get; set; }
    public double PickupLongitude { get; set; }
    public string DestinationLabel { get; set; } = string.Empty;
    public string DestinationAddress { get; set; } = string.Empty;
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public decimal? ServerPrice { get; set; }
    public decimal? CustomerPrice { get; set; }
    public decimal ServiceFee { get; set; }
    public decimal CancellationFee { get; set; }
    public decimal? TotalAmount { get; set; }
    public decimal DriverCommissionAmount { get; set; }
    public decimal? DriverShare { get; set; }
    public decimal? PlatformShare { get; set; }
    public string? RoutePolyline { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public ICollection<RideOffer> Offers { get; set; } = new List<RideOffer>();
    public ICollection<LocationUpdate> LocationUpdates { get; set; } = new List<LocationUpdate>();
}

public sealed class RideOffer : Entity
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public int DriverId { get; set; }
    public User Driver { get; set; } = null!;
    public decimal Amount { get; set; }
    public OfferStatus Status { get; set; } = OfferStatus.Pending;
    public DateTime ExpiresAtUtc { get; set; }
    public string? Note { get; set; }
}

public sealed class Wallet : Entity
{
    public byte[] RowVersion { get; set; } = [];
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "YER";
    public ICollection<WalletTransaction> Transactions { get; set; } = new List<WalletTransaction>();
}

public sealed class WalletTransaction : Entity
{
    public int WalletId { get; set; }
    public Wallet Wallet { get; set; } = null!;
    public int? RideId { get; set; }
    public WalletTransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ExternalReference { get; set; }
}

public sealed class LocationUpdate : Entity
{
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public int ActorId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Bearing { get; set; }
    public double? Speed { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Notification : Entity
{
    public int UserId { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? DataJson { get; set; }
    public bool IsRead { get; set; }
}

public sealed class PaymentTransaction : Entity
{
    public int UserId { get; set; }
    public int? RideId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "YER";
    public string Provider { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? ProviderReference { get; set; }
    public string? IdempotencyKey { get; set; }
}

/// <summary>Non-financial approval gate for a cash shortfall funded from the customer's wallet.</summary>
public sealed class CashCollectionApproval : Entity
{
    public int RideId { get; set; }
    public int DriverId { get; set; }
    public int CustomerId { get; set; }
    public decimal CashReceived { get; set; }
    public decimal WalletDebitAmount { get; set; }
    public string Currency { get; set; } = "YER";
    public CashCollectionApprovalStatus Status { get; set; } = CashCollectionApprovalStatus.Pending;
    public string? IdempotencyKey { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>
/// Non-financial confirmation workflow used when a customer selects cash only
/// after the driver has completed the trip without recording a collection.
/// </summary>
public sealed class CashPaymentRequest : Entity
{
    public int RideId { get; set; }
    public int CustomerId { get; set; }
    public int DriverId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "YER";
    public CashPaymentRequestStatus Status { get; set; } = CashPaymentRequestStatus.Pending;
    public string? IdempotencyKey { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>
/// An auditable cancellation request. It is intentionally separate from the
/// ride status so a stopped ride can wait for a driver or administrator
/// decision without creating or changing any financial movement prematurely.
/// </summary>
public sealed class RideCancellationRequest : Entity
{
    public byte[] RowVersion { get; set; } = [];
    public int RideId { get; set; }
    public Ride Ride { get; set; } = null!;
    public int CustomerId { get; set; }
    public int? DriverId { get; set; }
    public RideStatus RideStatusAtRequest { get; set; }
    public RideCancellationStatus Status { get; set; }
    public RideCancellationDriverDecision DriverDecision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public CashCancellationRefundMethod? RequestedRefundMethod { get; set; }
    public decimal? RequestedRefundAmount { get; set; }
    public double? CancellationLatitude { get; set; }
    public double? CancellationLongitude { get; set; }
    public DateTime? LocationObservedAtUtc { get; set; }
    public DateTime? DriverDecidedAtUtc { get; set; }
    public string? DriverNote { get; set; }
    public int? AdminUserId { get; set; }
    public DateTime? AdminDecidedAtUtc { get; set; }
    public string? AdminNote { get; set; }
}

public sealed class PaymentCardToken : Entity
{
    public int UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string LastFour { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public int ExpiryMonth { get; set; }
    public int ExpiryYear { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>Public, non-secret catalog data for an external payment channel.</summary>
public sealed class PaymentMethod : Entity
{
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string DescriptionAr { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public PaymentMethodKind Kind { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string? PublicInstructionsAr { get; set; }
    public bool IsActive { get; set; }
    public bool IsAvailableForRidePayment { get; set; }
    public bool IsAvailableForWalletTopUp { get; set; }
    public int SortOrder { get; set; }
}

public sealed class Promotion : Entity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal? FixedDiscount { get; set; }
    public decimal? PercentageDiscount { get; set; }
    public decimal? MaximumDiscount { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public int? UsageLimit { get; set; }
    public int UsageCount { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SupportTicket : Entity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string? AdminReply { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}

public sealed class ReferralRedemption : Entity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Code { get; set; } = string.Empty;
    public string Status { get; set; } = "Submitted";
}

public sealed class LedgerAccount : Entity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public LedgerAccountType Type { get; set; }
    public LedgerAccountPurpose Purpose { get; set; } = LedgerAccountPurpose.General;
    public string Currency { get; set; } = "YER";
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
    public bool IsPosting { get; set; } = true;
    public int? ParentLedgerAccountId { get; set; }
    public LedgerAccount? ParentLedgerAccount { get; set; }
    public ICollection<LedgerAccount> ChildLedgerAccounts { get; set; } = new List<LedgerAccount>();
    public int? FinancialPartyId { get; set; }
    public FinancialParty? FinancialParty { get; set; }
}

/// <summary>
/// A neutral financial owner. EntityId is deliberately generic so an account
/// may belong to a user, wallet, bank, cash box, provider, or future entity
/// without duplicating account tables for each owner type.
/// </summary>
public sealed class FinancialParty : Entity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public FinancialPartyType Type { get; set; }
    public int? EntityId { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<LedgerAccount> LedgerAccounts { get; set; } = new List<LedgerAccount>();
}

public sealed class JournalEntry : Entity
{
    public string EntryNumber { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public JournalEntryType Type { get; set; }
    public JournalEntryStatus Status { get; set; } = JournalEntryStatus.Draft;
    public string Description { get; set; } = string.Empty;
    public string Currency { get; set; } = "YER";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime PostedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsPosted { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public string? SourceType { get; set; }
    public string? SourceId { get; set; }
    public string? IdempotencyKey { get; set; }
    public int? ReversesJournalEntryId { get; set; }
    public JournalEntry? ReversesJournalEntry { get; set; }
    public ICollection<JournalEntry> ReversalEntries { get; set; } = new List<JournalEntry>();
    public int? CreatedByUserId { get; set; }
    public int? PostedByUserId { get; set; }
    public ICollection<JournalLine> Lines { get; set; } = new List<JournalLine>();
}

public sealed class JournalLine : Entity
{
    public int JournalEntryId { get; set; }
    public JournalEntry JournalEntry { get; set; } = null!;
    public int LedgerAccountId { get; set; }
    public LedgerAccount LedgerAccount { get; set; } = null!;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Currency { get; set; } = "YER";
    public string Description { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public int? FinancialPartyId { get; set; }
    public FinancialParty? FinancialParty { get; set; }
    public int? UserId { get; set; }
    public int? RideId { get; set; }
}

public sealed class DriverSettlement : Entity
{
    public int DriverId { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public decimal GrossRideAmount { get; set; }
    public decimal PlatformCommission { get; set; }
    public decimal Adjustments { get; set; }
    public decimal NetPayable { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? PaymentReference { get; set; }
}

/// <summary>
/// An immutable confirmation that the platform has collected all or part of a
/// cash-debt settlement from a driver. The originating ride settlement is
/// intentionally never edited or deleted.
/// </summary>
public sealed class DriverSettlementPayment : Entity
{
    public int DriverSettlementId { get; set; }
    public int DriverId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "YER";
    public string Method { get; set; } = "CashToPlatform";
    public string Reference { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int SettledByUserId { get; set; }
}

public sealed class CommunicationMessage : Entity
{
    public int RideId { get; set; }
    public int SenderId { get; set; }
    public int RecipientId { get; set; }
    public string MessageType { get; set; } = "Text";
    public string Content { get; set; } = string.Empty;
    public DateTime? ReadAtUtc { get; set; }
}

public sealed class EmergencyRecording : Entity
{
    public int UserId { get; set; }
    public int? RideId { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public long UploadedBytes { get; set; }
    public long DurationMilliseconds { get; set; }
    public DateTime ConsentAtUtc { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? LastChunkAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}

public sealed class PricingRule : Entity
{
    public int ServiceKindId { get; set; }
    public ServiceKind ServiceKind { get; set; } = null!;
    public int ServiceCatalogItemId { get; set; }
    public RideServiceCatalogItem ServiceCatalogItem { get; set; } = null!;
    public decimal BaseFare { get; set; }
    public decimal PerKilometer { get; set; }
    public decimal PerMinute { get; set; }
    public decimal ServiceFee { get; set; }
    public decimal DriverCommissionRate { get; set; }
    public decimal DriverCommissionFixed { get; set; }
    public decimal CancellationFee { get; set; }
    public decimal DriverShareRate { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class RideServiceCatalogItem : Entity
{
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public int ServiceKindId { get; set; }
    public ServiceKind ServiceKind { get; set; } = null!;
    public int ArrivalMinutes { get; set; }
    public decimal BasePrice { get; set; }
    public decimal Rating { get; set; }
    public int Seats { get; set; }
    public string DescriptionAr { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public bool IsRecommended { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class ServiceKind : Entity
{
    public string Code { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
}
