using System.ComponentModel.DataAnnotations;

namespace TechnoSurfacesApp.Models;

/// <summary>Posted by the Users screen's create form.</summary>
public sealed class CreateUserForm
{
    [Required, StringLength(200)]
    public string FullName { get; set; } = "";

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = "";

    [Required, RegularExpression("^(ManagingDirector|Estimator)$")]
    public string Role { get; set; } = "";
}