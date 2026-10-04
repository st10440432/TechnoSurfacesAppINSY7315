namespace TechnoSurfaces.Application.Auditing;

/// <summary>Reads the audit trail (NFR-04, US-19). There is no write method: the table is insert-only.</summary>
public interface IAuditTrailService
{
    /// <summary>The audit trail screen: newest first, at most a fixed number of rows.</summary>
    Task<AuditPage> SearchAsync(AuditFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Every recorded change to one quote, its versions and its costing lines, including
    /// lines since removed. This is how an estimator sees the MD's corrections (US-19).
    /// </summary>
    Task<IReadOnlyList<AuditRow>> ForQuoteAsync(int quoteId, CancellationToken ct = default);

    Task<IReadOnlyList<AuditRow>> ForQuoteReferenceAsync(string reference, CancellationToken ct = default);

    Task<IReadOnlyList<string>> EntityNamesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AuditUserOption>> UsersAsync(CancellationToken ct = default);
}

/// <summary>What the audit trail screen filters on. Every field is optional.</summary>
public sealed record AuditFilter(
    string? UserId = null,
    string? EntityName = null,
    DateOnly? From = null,
    DateOnly? To = null,
    bool PriceOnly = false)
{
    public bool IsEmpty => this == new AuditFilter();
}

/// <summary>One recorded change to one property, with the user's name resolved.</summary>
public sealed record AuditRow(
    long Id, DateTime ChangedAtUtc, string UserId, string UserName,
    string EntityName, string EntityKey, string PropertyName, string? OldValue, string? NewValue)
{
    public bool IsPriceChange => EntityName is "MaterialPrice" or "RatePrice";
    public bool IsDeletion => PropertyName == "(deleted)";
    public string Action => IsDeletion ? "Removed" : OldValue is null ? "Set" : "Changed";
    public DateTime ChangedAtLocal => AuditTime.ToLocal(ChangedAtUtc);
}

public sealed record AuditPage(IReadOnlyList<AuditRow> Rows, int TotalMatching, bool Truncated);

/// <summary>A user who appears in the trail. The id goes in the URL, never the name or email (NFR-09).</summary>
public sealed record AuditUserOption(string UserId, string Name);

/// <summary>
/// Techno Surfaces works in South African time, UTC+2 all year with no daylight saving.
/// Audit rows are stored in UTC; they are shown, and filtered by day, in SAST.
/// </summary>
public static class AuditTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(2);

    public static DateTime ToLocal(DateTime utc) =>
        DateTime.SpecifyKind(utc + Offset, DateTimeKind.Unspecified);

    public static DateTime StartOfDayUtc(DateOnly day) =>
        DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue) - Offset, DateTimeKind.Utc);
}