using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Administration;
public sealed record OperationalPolicyModel(double? ArrivalToleranceMeters = null, int? PickupGraceMinutes = null, int? DecisionResponseMinutes = null, string? ApprovalBasis = null);
public sealed class OperationalPolicyService(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext user) : ModelService<OperationalPolicyModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "update"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, OperationalPolicyModel m, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        await AdministratorAccess.RequireAsync(db, actor, operation == "get" ? "policy.read" : "policy.write", ct);
        var current = await db.OperationalPolicies.OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (operation == "get") return new { current, notice = "القيم غير المعتمدة فارغة. لا تفرض رسوم عدم حضور أو مهلة مالية تلقائية. اعتماد جديد يخص الرحلات الجديدة فقط." };
        if (string.IsNullOrWhiteSpace(m.ApprovalBasis) || m.ApprovalBasis.Length > 2000 || m.ArrivalToleranceMeters is <= 0 or > 100000 || m.PickupGraceMinutes is < 0 or > 525600 || m.DecisionResponseMinutes is <= 0 or > 525600 ||
            m.ArrivalToleranceMeters.HasValue && !double.IsFinite(m.ArrivalToleranceMeters.Value))
            throw new ServiceException("policy_approval_required", "حدد أساس السياسة والقيم الموجبة أو اترك غير المعتمد فارغاً. لا تضاف أي رسوم جديدة.");
        current = new() { ArrivalToleranceMeters = m.ArrivalToleranceMeters, PickupGraceMinutes = m.PickupGraceMinutes,
            DecisionResponseMinutes = m.DecisionResponseMinutes, ApprovalBasis = m.ApprovalBasis.Trim(), ApprovedByUserId = actor };
        db.OperationalPolicies.Add(current);
        db.OperationalDecisionAudits.Add(new() { ActorUserId = actor, Subject = "OperationalPolicy", Action = "approve_version", Reason = current.ApprovalBasis,
            ResultJson = System.Text.Json.JsonSerializer.Serialize(m) });
        await db.SaveChangesAsync(ct); return current;
    }
}
