using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Domain.Auditing;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data.Auditing;

/// <summary>
/// Reads the audit trail. Read-only by design: the table is insert-only (a database
/// trigger refuses updates and deletes) and this class has no method that writes.
/// Who may call it is the CanViewAuditTrail policy on the controller.
/// </summary>
public sealed class AuditTrailService : IAuditTrailService
{
    /// <summary>The most rows one screen shows. The filters narrow the rest.</summary>
    public const int RowLimit = 500;

    private readonly TechnoSurfacesDbContext _db;

    public AuditTrailService(TechnoSurfacesDbContext db) => _db = db;

    public async Task<AuditPage> SearchAsync(AuditFilter filter, CancellationToken ct = default)
    {
        var query = _db.AuditEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.UserId))
            query = query.Where(a => a.UserId == filter.UserId);

        if (!string.IsNullOrWhiteSpace(filter.EntityName))
            query = query.Where(a => a.EntityName == filter.EntityName);

        if (filter.PriceOnly)
            query = query.Where(a => a.EntityName == nameof(MaterialPrice) || a.EntityName == nameof(RatePrice));

        // A reversed range is read the way the person meant it.
        var (from, to) = filter.From > filter.To ? (filter.To, filter.From) : (filter.From, filter.To);

        if (from is { } first)
        {
            var fromUtc = AuditTime.StartOfDayUtc(first);
            query = query.Where(a => a.ChangedAtUtc >= fromUtc);
        }

        // "To" includes the whole of that day, so the bound is the start of the next.
        if (to is { } last)
        {
            var beforeUtc = AuditTime.StartOfDayUtc(last.AddDays(1));
            query = query.Where(a => a.ChangedAtUtc < beforeUtc);
        }

        var total = await query.CountAsync(ct);
        var entries = await query
            .OrderByDescending(a => a.ChangedAtUtc).ThenByDescending(a => a.Id)
            .Take(RowLimit)
            .ToListAsync(ct);

        return new AuditPage(await ToRowsAsync(entries, ct), total, total > entries.Count);
    }

    public async Task<IReadOnlyList<AuditRow>> ForQuoteAsync(int quoteId, CancellationToken ct = default)
    {
        var quoteKey = Key(quoteId);

        var versionKeys = (await _db.QuoteVersions.AsNoTracking()
                .Where(v => v.QuoteId == quoteId)
                .Select(v => v.Id)
                .ToListAsync(ct))
            .Select(Key)
            .ToList();

        // A costing line can be removed, but the audit row written when it was added
        // still records which version it belonged to, so its history stays visible.
        var lineKeys = await _db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityName == nameof(CostingLine)
                     && a.PropertyName == nameof(CostingLine.QuoteVersionId)
                     && a.NewValue != null
                     && versionKeys.Contains(a.NewValue!))
            .Select(a => a.EntityKey)
            .Distinct()
            .ToListAsync(ct);

        var entries = await _db.AuditEntries.AsNoTracking()
            .Where(a => (a.EntityName == nameof(Quote) && a.EntityKey == quoteKey)
                     || (a.EntityName == nameof(QuoteVersion) && versionKeys.Contains(a.EntityKey))
                     || (a.EntityName == nameof(CostingLine) && lineKeys.Contains(a.EntityKey)))
            .OrderByDescending(a => a.ChangedAtUtc).ThenByDescending(a => a.Id)
            .ToListAsync(ct);

        return await ToRowsAsync(entries, ct);
    }

    public async Task<IReadOnlyList<AuditRow>> ForQuoteReferenceAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return [];

        var quoteId = await _db.Quotes.AsNoTracking()
            .Where(q => q.Reference == reference)
            .Select(q => (int?)q.Id)
            .SingleOrDefaultAsync(ct);

        // An unknown reference has no recorded history, rather than someone else's.
        return quoteId is { } id ? await ForQuoteAsync(id, ct) : [];
    }

    public async Task<IReadOnlyList<string>> EntityNamesAsync(CancellationToken ct = default) =>
        await _db.AuditEntries.AsNoTracking()
            .Select(a => a.EntityName)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AuditUserOption>> UsersAsync(CancellationToken ct = default)
    {
        var ids = await _db.AuditEntries.AsNoTracking().Select(a => a.UserId).Distinct().ToListAsync(ct);
        var names = await NamesAsync(ids, ct);

        return ids
            .Select(id => new AuditUserOption(id, NameOf(id, names)))
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<AuditRow>> ToRowsAsync(List<AuditEntry> entries, CancellationToken ct)
    {
        var names = await NamesAsync(entries.Select(e => e.UserId).Distinct().ToList(), ct);

        return entries
            .Select(e => new AuditRow(e.Id, e.ChangedAtUtc, e.UserId, NameOf(e.UserId, names),
                e.EntityName, e.EntityKey, e.PropertyName, e.OldValue, e.NewValue))
            .ToList();
    }

    private Task<Dictionary<string, string>> NamesAsync(List<string> userIds, CancellationToken ct) =>
        _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

    private static string NameOf(string userId, Dictionary<string, string> names) =>
        userId == ICurrentUser.SystemUserId ? "System"
        : names.TryGetValue(userId, out var name) && !string.IsNullOrWhiteSpace(name) ? name
        : userId;

    private static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);
}