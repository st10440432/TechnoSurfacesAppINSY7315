using System.ComponentModel.DataAnnotations;

namespace TechnoSurfacesApp.Models;

/// <summary>The "Set your password" form shown after signing in with a temporary password.</summary>
public sealed class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Enter the temporary password you were given.")]
    [DataType(DataType.Password)]
    [Display(Name = "Temporary password")]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "Enter a new password.")]
    [StringLength(128, MinimumLength = 12, ErrorMessage = "Use at least 12 characters.")]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    [Required(ErrorMessage = "Enter the new password again.")]
    [Compare(nameof(NewPassword), ErrorMessage = "The two passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";
}