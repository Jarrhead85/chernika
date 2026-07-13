using Chernika.Domain.Entities;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Chernika.Web.Pages.Account;

[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string ErrorMessage { get; set; } = "";

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Заполните все поля";
            return Page();
        }

        var user = await _userManager.FindByNameAsync(Username);
        if (user == null)
        {
            ErrorMessage = "Неверный логин или пароль";
            return Page();
        }

        if (!user.IsActive)
        {
            ErrorMessage = "Аккаунт заблокирован администратором";
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(user, Password, isPersistent: true, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return Redirect("/%D1%80%D0%B5%D0%B5%D1%81%D1%82%D1%80-%D1%85%D0%BA");
        }

        if (result.IsLockedOut)
        {
            ErrorMessage = "Аккаунт заблокирован. Попробуйте через 15 минут.";
        }
        else
        {
            ErrorMessage = "Неверный логин или пароль";
        }

        return Page();
    }
}
