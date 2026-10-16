using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Application.Abstractions;
using Application.Common;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Staff;
using Infrastructure.Identity;
using Infrastructure.MultiTenancy;
using Infrastructure.Persistence;

namespace Infrastructure.Seeding;

/// <summary>
/// Development-only demo data: logins for every role, merchants with fixed, documented API keys and payout accounts, and
/// riders. Tenants, hubs, zones, areas and rates are NOT created here - they come from the DbUp seed - because this
/// needs Identity to hash passwords, which SQL cannot do. A tenant listed here that does not exist is skipped (the
/// "rival" courier exists only in the integration tests' database). Safe to run on every start: each row is created
/// only when missing. With <c>Seed:DemoActivity</c>, a courier with no parcels also gets a spread of sample parcels.
/// </summary>
public class DemoDataSeeder(
    IServiceScopeFactory scopeFactory,
    ITenantCatalog catalog,
    ILogger<DemoDataSeeder> logger)
{
    public const string PlatformAdmin = "admin@platform.test";

    private static readonly DemoTenant[] Tenants =
    [
        new(
            "onedrop",
            [
                // The doors are pinned, as a merchant pins them, so the rider's round has a route to follow
                new("fashion", "Fashion House", "Nusrat Jahan", "Mirpur 10", "House 7, Road 2, Mirpur 10", "01711000001", PayoutMethod.Bkash, MerchantStatus.Active, "od_odfashion001_DevOnlyKeyDoNotUseInProduction01", "23.806964,90.368393"),
                new("gadget", "Gadget BD", "Tanvir Ahmed", "Dhanmondi", "Shop 12, Road 27, Dhanmondi", "01711000002", PayoutMethod.Bank, MerchantStatus.Active, "od_odgadget0001_DevOnlyKeyDoNotUseInProduction02", "23.746466,90.376015"),
                new("beauty", "Beauty Shop", "Sharmin Akter", "Uttara Sector 7", "House 30, Road 14, Uttara Sector 7", "01711000003", PayoutMethod.Nagad, MerchantStatus.Active, "od_odbeauty0001_DevOnlyKeyDoNotUseInProduction03", "23.874281,90.379845"),
                new("crafts", "Chattogram Crafts", "Imran Hossain", "Agrabad", "CDA Avenue, Agrabad", "01711000004", PayoutMethod.Bkash, MerchantStatus.Active, "od_odctgcraft01_DevOnlyKeyDoNotUseInProduction04", "22.327564,91.805061"),
                new("organic", "Organic Bazar", "Farzana Islam", "Mohammadpur", "Ring Road, Mohammadpur", "01711000005", null, MerchantStatus.Pending, null)
            ],
            [
                new("rider", "Rafiq Hasan", "MIR", "01722000001", Vehicle.Motorbike),
                new("rider2", "Sumon Ali", "MIR", "01722000002", Vehicle.Bicycle),
                new("rider3", "Kamal Uddin", "GUL", "01722000003", Vehicle.Motorbike),
                new("rider4", "Jamal Chowdhury", "CTG", "01722000004", Vehicle.Motorbike),
                new("rider5", "Nasir Uddin", "DHN", "01722000005", Vehicle.Van),
                new("rider6", "Habib Rahman", "UTT", "01722000006", Vehicle.Bicycle),
                // Every hub the demo collects from needs a pickup van: nothing else goes to a merchant's door
                new("van", "Shahin Mollah", "MIR", "01722000007", Vehicle.Van),
                new("van2", "Babul Akter", "GUL", "01722000008", Vehicle.Van),
                new("van3", "Delwar Hossain", "CTG", "01722000009", Vehicle.Van),
                new("van4", "Monir Sheikh", "UTT", "01722000010", Vehicle.Van)
            ],
            [
                new("hub", "Hub staff", StaffJob.Relief, null),
                new("mirpur", "Karim Mia", StaffJob.HubManager, "MIR"),
                new("receiving", "Rahim Uddin", StaffJob.Receiving, "MIR"),
                new("accounts", "Salma Begum", StaffJob.Accounts, null)
            ]),
        new(
            "rival",
            [
                new("shop", "Rival Shop", "Rival Owner", "Rival Town", "Main Road, Rival Town", "01799000001", PayoutMethod.Bkash, MerchantStatus.Active, "od_rivalshop001_DevOnlyKeyDoNotUseInProduction09")
            ],
            [
                new("rider", "Rival Rider", "RVL", "01799000002", Vehicle.Motorbike),
                new("van", "Rival Van", "RVL", "01799000003", Vehicle.Van)
            ],
            [
                new("hub", "Hub staff", StaffJob.Relief, null)
            ])
    ];

    public async Task SeedAsync(string password, bool activity, CancellationToken cancellationToken = default)
    {
        await using (var platformScope = scopeFactory.CreateAsyncScope())
        {
            await EnsureUserAsync(platformScope, PlatformAdmin, "Platform admin", Roles.PlatformAdmin, password, null, null);
        }

        foreach (var demo in Tenants)
        {
            var tenant = await catalog.FindBySlugAsync(demo.Slug, cancellationToken);
            if (tenant is null)
            {
                logger.LogInformation("Demo seed skipped {Slug}: no such courier here (dbup creates the launch courier; the rival exists only in the test database)", demo.Slug);
                continue;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);

            await EnsureUserAsync(scope, $"admin@{demo.Slug}.test", $"{tenant.Name} admin", Roles.TenantAdmin, password, tenant.Id, null);
            foreach (var person in demo.Staff)
            {
                var userId = await EnsureUserAsync(
                    scope, $"{person.Key}@{demo.Slug}.test", person.Name, Roles.HubStaff, password, tenant.Id, null);
                await EnsureStaffAsync(scope, userId, person, cancellationToken);
            }

            foreach (var merchant in demo.Merchants)
            {
                var merchantId = await EnsureMerchantAsync(scope, merchant, cancellationToken);
                if (merchantId.HasValue)
                {
                    await EnsureUserAsync(scope, $"{merchant.Key}@{demo.Slug}.test", merchant.Owner, Roles.Merchant, password, tenant.Id, merchantId);
                }
            }

            foreach (var rider in demo.Riders)
            {
                var userId = await EnsureUserAsync(scope, $"{rider.Key}@{demo.Slug}.test", rider.Name, Roles.Rider, password, tenant.Id, null);
                await EnsureRiderAsync(scope, userId, rider, cancellationToken);
            }

            if (activity && demo.Slug == "onedrop")
            {
                await ActivatorUtilities.CreateInstance<DemoActivity>(scope.ServiceProvider).SeedAsync(cancellationToken);
            }
        }

        logger.LogInformation("Demo data ready. Logins and API keys are listed in README.md");
    }

    private async Task<long?> EnsureMerchantAsync(AsyncServiceScope scope, DemoMerchant demo, CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.Merchants.FirstOrDefaultAsync(m => m.Name == demo.Name, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var area = await db.Areas.FirstOrDefaultAsync(a => a.Name == demo.Area, cancellationToken);
        if (area is null)
        {
            logger.LogWarning("Demo merchant {Merchant} skipped: area {Area} does not exist", demo.Name, demo.Area);
            return null;
        }

        var profile = new MerchantProfile(demo.Name, demo.Owner, demo.Phone, $"{demo.Key}@example.com", $"{demo.Address}, {area.Name}");
        var merchant = (demo.Status == MerchantStatus.Pending ? Merchant.SignUp(profile) : Merchant.Add(profile)).Value;
        if (demo.Payout is { } method)
        {
            merchant.SetPayoutAccount(
                method,
                method == PayoutMethod.Bank ? "1501203456789" : demo.Phone,
                method == PayoutMethod.Bank ? $"{demo.Owner}, City Bank, Dhanmondi branch" : demo.Owner);
        }

        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(cancellationToken);

        db.PickupPoints.Add(
            PickupPoint.Create(merchant.Id, area.Id, "Shop", demo.Address, demo.Phone, isDefault: true, GeoPoint.Find(demo.Pin)).Value);
        if (demo.ApiKey is not null)
        {
            db.MerchantApiKeys.Add(MerchantApiKey.FromPlaintext(merchant.Id, "Demo website", demo.ApiKey).Value);
        }

        await db.SaveChangesAsync(cancellationToken);

        return merchant.Id;
    }

    /// <summary>Creates the login when missing. Returns its id either way.</summary>
    private static async Task<long> EnsureUserAsync(
        AsyncServiceScope scope,
        string email,
        string displayName,
        string role,
        string password,
        long? tenantId,
        long? merchantId)
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByNameAsync(email) is { } existing)
        {
            return existing.Id;
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            TenantId = tenantId,
            MerchantId = merchantId,
            Created = DateTime.UtcNow
        };
        var created = await users.CreateAsync(user, password);
        if (created.Succeeded)
        {
            created = await users.AddToRoleAsync(user, role);
        }

        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not seed {email}: {string.Join("; ", created.Errors.Select(e => e.Description))}");
        }

        return user.Id;
    }

    private async Task EnsureRiderAsync(AsyncServiceScope scope, long userId, DemoRider demo, CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Riders.AnyAsync(rider => rider.UserId == userId, cancellationToken))
        {
            return;
        }

        var hub = await db.Hubs.FirstOrDefaultAsync(h => h.Code == demo.Hub, cancellationToken);
        if (hub is null)
        {
            logger.LogWarning("Demo rider {Rider} skipped: hub {Hub} does not exist", demo.Name, demo.Hub);
            return;
        }

        db.Riders.Add(Rider.Create(hub.Id, demo.Name, demo.Phone, demo.Vehicle, userId).Value);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A staff login with its job, at head office or at a hub, when missing.</summary>
    private async Task EnsureStaffAsync(AsyncServiceScope scope, long userId, DemoStaff demo, CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.StaffMembers.AnyAsync(staff => staff.UserId == userId, cancellationToken))
        {
            return;
        }

        var hub = demo.Hub is null ? null : await db.Hubs.FirstOrDefaultAsync(h => h.Code == demo.Hub, cancellationToken);
        if (demo.Hub is not null && hub is null)
        {
            logger.LogWarning("Demo staff {Name} skipped: hub {Hub} does not exist", demo.Name, demo.Hub);
            return;
        }

        db.StaffMembers.Add(StaffMember.Add(userId, hub?.Id, demo.Job, StaffPermissions.None).Value);
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record DemoTenant(string Slug, DemoMerchant[] Merchants, DemoRider[] Riders, DemoStaff[] Staff);

    private sealed record DemoMerchant(
        string Key,
        string Name,
        string Owner,
        string Area,
        string Address,
        string Phone,
        PayoutMethod? Payout,
        MerchantStatus Status,
        string? ApiKey,
        string? Pin = null);

    private sealed record DemoRider(string Key, string Name, string Hub, string Phone, Vehicle Vehicle);

    private sealed record DemoStaff(string Key, string Name, StaffJob Job, string? Hub);
}
