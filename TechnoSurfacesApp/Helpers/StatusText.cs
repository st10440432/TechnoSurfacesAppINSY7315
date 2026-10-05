namespace TechnoSurfacesApp.Helpers;

/// <summary>
/// How a quote status reads on screen. Every screen uses these, so a status is
/// always written the same way and always carries its name as well as its colour.
/// </summary>
public static class StatusText
{
    public static string Label(string status) => status switch
    {
        "PendingApproval" => "Pending approval",
        _ => status
    };

    /// <summary>The CSS class for the status pill.</summary>
    public static string Pill(string status) => "pill pill-" + status.ToLowerInvariant();

    /// <summary>The statuses in the order a quote moves through them, for filters.</summary>
    public static readonly string[] All =
        { "Draft", "PendingApproval", "Approved", "Sent", "Accepted", "Expired" };
}
