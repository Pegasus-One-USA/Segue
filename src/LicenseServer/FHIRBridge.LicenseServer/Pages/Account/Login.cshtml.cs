using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using FHIRBridge.LicenseServer.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FHIRBridge.LicenseServer.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly AdminAccountProvider _adminAccountProvider;

    public LoginModel(AdminAccountProvider adminAccountProvider)
    {
        _adminAccountProvider = adminAccountProvider;
    }

    [BindProperty]
    [Required]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public string? GeneratedPasswordHint =>
        _adminAccountProvider.IsGeneratedPassword
            ? "No admin password is configured yet — check this process's startup logs for a randomly generated one-time password."
            : null;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = "Username and password are required.";
            return Page();
        }

        if (!_adminAccountProvider.Verify(Username, Password))
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, _adminAccountProvider.Username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Index");
    }
}
