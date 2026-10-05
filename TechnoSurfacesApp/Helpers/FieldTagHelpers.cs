using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace TechnoSurfacesApp.Helpers;

/// <summary>
/// Marks a form control that failed validation. Adds aria-invalid and points
/// aria-describedby at the field's error message, so a screen reader reads the
/// error when the control gains focus. Runs on every input, select and textarea
/// bound with asp-for, so no form has to remember to do it.
/// </summary>
[HtmlTargetElement("input", Attributes = ForAttributeName)]
[HtmlTargetElement("select", Attributes = ForAttributeName)]
[HtmlTargetElement("textarea", Attributes = ForAttributeName)]
public sealed class AriaInvalidTagHelper : TagHelper
{
    private const string ForAttributeName = "asp-for";

    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression For { get; set; } = null!;

    [ViewContext, HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    /// <summary>After the framework's own tag helpers have written the id and name.</summary>
    public override int Order => 1000;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var name = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        if (!FieldErrors.Has(ViewContext, name))
            return;

        output.Attributes.SetAttribute("aria-invalid", "true");
        var errorId = FieldErrors.IdFor(name);
        var existing = output.Attributes.TryGetAttribute("aria-describedby", out var attribute)
            ? attribute.Value?.ToString()
            : null;
        output.Attributes.SetAttribute("aria-describedby",
            string.IsNullOrWhiteSpace(existing) ? errorId : existing + " " + errorId);
    }
}

/// <summary>
/// The error message under a field: <c>&lt;field-error for="Form.Reference" /&gt;</c>.
/// Renders nothing when the field is valid. Its id is the one the control's
/// aria-describedby points at.
/// </summary>
[HtmlTargetElement("field-error", Attributes = "for", TagStructure = TagStructure.WithoutEndTag)]
public sealed class FieldErrorTagHelper : TagHelper
{
    [HtmlAttributeName("for")]
    public ModelExpression For { get; set; } = null!;

    [ViewContext, HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var name = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        var message = FieldErrors.MessageFor(ViewContext, name);
        if (message is null)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("id", FieldErrors.IdFor(name));
        output.Attributes.SetAttribute("class", "field-error");
        output.Content.SetContent(message);
    }
}

/// <summary>Reads validation errors from model state for the two tag helpers and the error summary.</summary>
public static class FieldErrors
{
    public static string IdFor(string fullName) => TagBuilder.CreateSanitizedId(fullName, "_") + "-error";

    public static string ControlIdFor(string fullName) => TagBuilder.CreateSanitizedId(fullName, "_");

    public static bool Has(ViewContext view, string fullName) =>
        view.ViewData.ModelState.TryGetValue(fullName, out var entry) && entry.Errors.Count > 0;

    public static string? MessageFor(ViewContext view, string fullName) =>
        view.ViewData.ModelState.TryGetValue(fullName, out var entry) && entry.Errors.Count > 0
            ? string.Join(" ", entry.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "This value is not valid." : e.ErrorMessage))
            : null;
}
