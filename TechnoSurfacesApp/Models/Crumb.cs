namespace TechnoSurfacesApp.Models;

/// <summary>
/// One step of the breadcrumb at the top of a page. The last step is the current
/// page and has no link.
/// </summary>
public sealed record Crumb(string Label, string? Url = null);
