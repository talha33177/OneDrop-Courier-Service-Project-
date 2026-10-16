using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Application.Abstractions;
using Application.Merchants.Account;
using Application.Merchants.PayoutAccounts;
using Application.Network.ListAreas;
using Domain.Common;
using Domain.Merchants;
using Web.Display;

namespace Web.Pages.Merchant;

/// <summary>The merchant's own settings: business profile, payout account and pickup points.</summary>
public class SettingsModel(
    MerchantAccountHandler account,
    MerchantPictureHandler pictures,
    PayoutAccountsHandler payoutAccounts,
    ICurrentUser currentUser,
    ListAreasHandler areas) : PageModel
{
    /// <summary>The picture's address, or null when the business has none.</summary>
    public string? PictureUrl { get; private set; }

    public long MerchantId => currentUser.MerchantId ?? 0;

    public MerchantAccount Account { get; private set; } = null!;

    public PayoutAccountsView PayoutAccounts { get; private set; } = null!;

    public IReadOnlyList<AreaItem> Areas { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostPictureAsync(IFormFile? picture, CancellationToken cancellationToken)
    {
        // One byte past the most, so a file that is too big is refused without reading all of it
        var content = new byte[(int)Math.Min(picture?.Length ?? 0, MerchantPicture.MaxBytes + 1)];
        if (picture is not null)
        {
            await using var stream = picture.OpenReadStream();
            await stream.ReadExactlyAsync(content, cancellationToken);
        }

        return Answer(await pictures.SetAsync(content, cancellationToken), "Your picture is saved.");
    }

    public async Task<IActionResult> OnPostRemovePictureAsync(CancellationToken cancellationToken)
    {
        await pictures.RemoveAsync(cancellationToken);
        TempData["Done"] = "Your picture is removed.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProfileAsync(
        string? name,
        string? owner,
        string? phone,
        string? email,
        string? address,
        CancellationToken cancellationToken)
    {
        var saved = await account.EditProfileAsync(new MerchantProfile(name, owner, phone, email, address), cancellationToken);

        return Answer(saved, "Your profile is saved.");
    }

    /// <summary>Keeps a payout account; payouts go there unless <paramref name="use"/> is false.</summary>
    public async Task<IActionResult> OnPostPayoutAsync(
        PayoutMethod method,
        string? payoutAccount,
        string? accountName,
        bool? use,
        CancellationToken cancellationToken)
    {
        var send = use ?? true;
        var saved = await payoutAccounts.AddAsync(method, payoutAccount, accountName, send, cancellationToken);

        return Answer(
            saved,
            send ? "Your payout account is saved. Payouts go there from now on." : "The account is saved. Choose it for payouts whenever you like.");
    }

    public async Task<IActionResult> OnPostUsePayoutAsync(long id, CancellationToken cancellationToken)
    {
        return Answer(await payoutAccounts.UseAsync(id, cancellationToken), "Payouts go to that account from now on.");
    }

    public async Task<IActionResult> OnPostRemovePayoutAsync(long id, CancellationToken cancellationToken)
    {
        return Answer(await payoutAccounts.RemoveAsync(id, cancellationToken), "The account is removed.");
    }

    public async Task<IActionResult> OnPostDefaultAsync(long id, CancellationToken cancellationToken)
    {
        var saved = await account.MakeDefaultAsync(id, cancellationToken);

        return Answer(saved, "Default pickup point changed.");
    }

    private IActionResult Answer(Result result, string done)
    {
        if (result.IsFailure && result.Error!.Type == ErrorType.NotFound)
        {
            return NotFound();
        }

        TempData[result.IsSuccess ? "Done" : "Problem"] = result.IsSuccess ? done : result.Error!.Message;

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Account = await account.GetAsync(cancellationToken);
        PayoutAccounts = await payoutAccounts.GetAsync(cancellationToken);
        PictureUrl = Pictures.Url(MerchantId, await pictures.ChangedAsync(cancellationToken));
        Areas = await areas.HandleAsync(cancellationToken);
    }
}
