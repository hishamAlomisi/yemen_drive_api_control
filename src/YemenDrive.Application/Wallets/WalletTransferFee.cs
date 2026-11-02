using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Wallets;

public sealed record WalletTransferFeeModel(int? Id = null, decimal FromAmount = 0, decimal? ToAmount = null,
    string FeeKind = "Fixed", decimal FeeValue = 0, bool IsActive = true);

public sealed class WalletTransferFee(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext actor)
    : ModelService<WalletTransferFeeModel>(config)
{
    public override IReadOnlyCollection<string> Operations => ["list", "add", "update"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, WalletTransferFeeModel m, CancellationToken ct)
    {
        await AdministratorAccess.RequireAsync(db, actor.RequireUserId(), operation == "list" ? "finance.read" : "finance.write", ct);
        if (operation == "list") return await db.WalletTransferFeeRules.AsNoTracking().OrderBy(x => x.FromAmount).ToListAsync(ct);
        if (m.FromAmount < 0 || m.ToAmount is decimal end && end <= m.FromAmount || m.FeeValue < 0 ||
            m.FromAmount != decimal.Round(m.FromAmount, 2) || m.ToAmount != (m.ToAmount is decimal to ? decimal.Round(to, 2) : null) ||
            m.FeeValue != decimal.Round(m.FeeValue, 2) || m.FeeKind is not ("Fixed" or "Percent") || m.FeeKind == "Percent" && m.FeeValue > 100 ||
            m.FromAmount > 1_000_000_000 || m.ToAmount > 1_000_000_000 || m.FeeValue > 1_000_000_000)
            throw new ServiceException("invalid_transfer_fee", "أدخل شريحة صحيحة وقيمة رسوم موجبة أو صفراً، ونسبة لا تتجاوز 100٪.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var editingId = operation == "update" ? m.Id ?? 0 : 0;
        if (m.IsActive && await db.WalletTransferFeeRules.AnyAsync(x => x.IsActive && x.Id != editingId &&
            (m.ToAmount == null || x.FromAmount < m.ToAmount) && (x.ToAmount == null || x.ToAmount > m.FromAmount), ct))
            throw new ServiceException("transfer_fee_overlap", "تتداخل هذه الشريحة مع شريحة رسوم مفعلة. الحد الأدنى مشمول والأعلى غير مشمول.");
        var row = operation == "update" ? await db.WalletTransferFeeRules.SingleOrDefaultAsync(x => x.Id == m.Id, ct)
            ?? throw new ServiceException("fee_rule_not_found", "شريحة الرسوم غير موجودة.") : new WalletTransferFeeRule();
        row.FromAmount = m.FromAmount; row.ToAmount = m.ToAmount; row.FeeKind = m.FeeKind;
        row.FeeValue = m.FeeValue; row.IsActive = m.IsActive; row.UpdatedByUserId = actor.RequireUserId(); row.UpdatedAtUtc = DateTime.UtcNow;
        if (operation != "update") db.WalletTransferFeeRules.Add(row);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row;
    }
}
