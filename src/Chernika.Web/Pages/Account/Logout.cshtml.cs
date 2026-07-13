using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Chernika.Web.Pages.Account;

public class LogoutModel : PageModel
{
    private readonly SignInManager<Domain.Entities.ApplicationUser> _signInManager;

    public LogoutModel(SignInManager<Domain.Entities.ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await _signInManager.SignOutAsync();
        return Redirect("/%D0%B2%D1%85%D0%BE%D0%B4");
    }
}
