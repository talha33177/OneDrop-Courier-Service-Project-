using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Infrastructure.MultiTenancy;

namespace Infrastructure.Seeding;

/// <summary>
/// A live courier's first admin, from <c>Setup:AdminEmail</c> and <c>Setup:AdminPassword</c>. A database deployed from
/// nothing holds the courier, its map and its rates but nobody who can sign in, and outside Development there is no demo
/// seeder. The login is created when missing and never touched once it exists, so its password stays the one its owner
/// chose (or changed it to) and the password setting can be removed after the first start.
/// </summary>
public class AdminSetup(IServiceScopeFactory scopeFactory, ITenantCatalog catalog, ILogger<AdminSetup> logger)
{
    /// <summary>True when the login was created now, false when one with this email was already there.</summary>
    public async Task<bool> EnsureCourierAdminAsync(
        string slug,
        string email,
        string name,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var tenant = await catalog.FindBySlugAsync(slug, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Setup: there is no courier '{slug}'. Deploy the database first (tools/db/publish.ps1).");

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
        var created = await scope.ServiceProvider
            .GetRequiredService<IUserAccounts>()
            .CreateAsync(new NewLogin(email, password, name, Roles.TenantAdmin), cancellationToken);
        if (created.Error is { Type: ErrorType.Conflict })
        {
            logger.LogInformation("Setup: {Email} already signs in at {Courier}; left as it is", email, tenant.Name);
            return false;
        }

        if (created.IsFailure)
        {
            throw new InvalidOperationException(
                $"Setup: could not create the admin {email} from Setup:AdminEmail and Setup:AdminPassword. " +
                created.Error!.Message);
        }

        logger.LogInformation("Setup: created the admin {Email} at {Courier}", email, tenant.Name);

        return true;
    }
}
