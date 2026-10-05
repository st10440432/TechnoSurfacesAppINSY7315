using TechnoSurfaces.Application.Quoting;

namespace TechnoSurfacesApp.Models;

/// <summary>
/// The one quote table every screen uses: the dashboard, the quote list, the
/// approval queue and a customer's history. The flags choose which columns show.
/// </summary>
public sealed record QuoteTable(
    IReadOnlyList<QuoteSummary> Rows,
    string Caption,
    bool ShowCustomer = true,
    bool ShowAuthor = true,
    bool ShowValidity = true,
    bool ShowWaiting = false,
    bool LinkToReview = false);
