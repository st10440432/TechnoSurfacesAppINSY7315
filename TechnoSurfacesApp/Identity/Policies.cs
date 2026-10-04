namespace TechnoSurfacesApp.Identity;

/// <summary>
/// The named authorisation policies. Each is defined once in Program.cs; controllers
/// and screens refer to these names and never test a role themselves (Task 1 7.2.3).
/// </summary>
public static class Policies
{
    public const string CanApproveQuote = nameof(CanApproveQuote);
    public const string CanEditCatalogue = nameof(CanEditCatalogue);
    public const string CanManageUsers = nameof(CanManageUsers);
    public const string CanViewAuditTrail = nameof(CanViewAuditTrail);
    public const string CanEditQuote = nameof(CanEditQuote);
}