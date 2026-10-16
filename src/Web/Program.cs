using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Application;
using Hangfire;
using Infrastructure;
using Infrastructure.Jobs;
using Infrastructure.Seeding;
using Application.Abstractions;
using Application.Common;
using Web.Authentication;
using Web.Display;
using Web.Live;
using Web.MultiTenancy;
using Web.Pages.Dev;
using Web.Pages.Hub;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific secrets (the database connection string) live in this git-ignored file, never in the repository.
// Environment variables and command-line arguments are added again after it so they still win.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Tenant} {Message:lj}{NewLine}{Exception}"));

builder.Services.Configure<TenancyOptions>(builder.Configuration.GetSection("Tenancy"));
builder.Services.AddSingleton(builder.Configuration.GetSection("GoogleMaps").Get<MapsOptions>() ?? new MapsOptions());
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
var runJobs = builder.Configuration.GetValue<bool>("Jobs:Server");
builder.Services.AddJobs(runJobs);
builder.Services.AddAppIdentity();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "app.auth";
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);

    // An API caller gets a status code, not the login page
    options.Events.OnRedirectToLogin = context => ApiAwareRedirect(context, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = context => ApiAwareRedirect(context, StatusCodes.Status403Forbidden);

    // A cookie shown on another courier's host is refused outright by TenantUserGuardMiddleware. Checking its security
    // stamp here would look the login up at the wrong courier, find nobody and quietly sign the visitor out instead
    options.Events.OnValidatePrincipal = context =>
    {
        var current = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().TenantId;
        var claimed = context.Principal?.FindFirst(AppClaims.TenantId)?.Value;

        return claimed == current?.ToString(CultureInfo.InvariantCulture)
            ? SecurityStampValidator.ValidatePrincipalAsync(context)
            : Task.CompletedTask;
    };
});
builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(Policies.Configure);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Public forms (tracking, merchant sign-up): enough for a person, too few for a script guessing codes
    options.AddPolicy("public", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IOperationsFeed, OperationsFeed>();
builder.Services.AddSingleton<ReceivedWebhooks>();
builder.Services.AddScoped<MerchantPermissionFilter>();
builder.Services.AddScoped<StaffPermissionFilter>();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Merchant", Policies.MerchantPortal);
    options.Conventions.AuthorizeFolder("/Platform", Policies.PlatformAdmin);
    options.Conventions.AuthorizeFolder("/Hub", Policies.Operations);
    options.Conventions.AuthorizeFolder("/Admin", Policies.Operations);

    // The staff filter goes first: it leaves the person's rights on the request, which RememberHub reads
    options.Conventions.AddFolderApplicationModelConvention(
        "/Hub",
        model => model.Filters.Add(new ServiceFilterAttribute(typeof(StaffPermissionFilter))));
    options.Conventions.AddFolderApplicationModelConvention(
        "/Admin",
        model => model.Filters.Add(new ServiceFilterAttribute(typeof(StaffPermissionFilter))));
    options.Conventions.AddFolderApplicationModelConvention("/Hub", model => model.Filters.Add(new RememberHub()));
    options.Conventions.AddFolderApplicationModelConvention(
        "/Merchant",
        model => model.Filters.Add(new ServiceFilterAttribute(typeof(MerchantPermissionFilter))));
    options.Conventions.AuthorizeFolder("/Rider", Policies.Rider);
});

var app = builder.Build();

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Seed:DemoData"))
{
    await app.Services
        .GetRequiredService<DemoDataSeeder>()
        .SeedAsync(
            app.Configuration["Seed:Password"] ?? throw new InvalidOperationException("Seed:Password is not set."),
            app.Configuration.GetValue<bool>("Seed:DemoActivity"));
}

// A live courier's first admin: a database deployed from nothing has nobody who can sign in (see AdminSetup)
if (app.Configuration["Setup:AdminEmail"] is { Length: > 0 } adminEmail)
{
    await app.Services
        .GetRequiredService<AdminSetup>()
        .EnsureCourierAdminAsync(
            app.Configuration["Tenancy:HomeCourier"] is { Length: > 0 } courier
                ? courier
                : throw new InvalidOperationException("Setup:AdminEmail needs Tenancy:HomeCourier, the courier to admin."),
            adminEmail,
            app.Configuration["Setup:AdminName"] is { Length: > 0 } adminName ? adminName : "Courier admin",
            app.Configuration["Setup:AdminPassword"]);
}

if (runJobs)
{
    app.Services.ScheduleJobs(app.Configuration);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// An empty error answer (a mistyped address, too many tries): a page for people, problem details for API callers
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api"),
    api => api.UseStatusCodePages());
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    pages => pages.UseStatusCodePagesWithReExecute("/Error"));
app.UseSerilogRequestLogging();
app.MapStaticAssets();
app.UseRouting();

// Tenant resolution -> authentication -> tenant/merchant guard -> authorization
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantUserGuardMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapRazorPages().WithStaticAssets();
app.MapHub<OperationsHub>(OperationsHub.Path);
// Every tenant's jobs in one view, so platform staff only. The policy replaces Hangfire's local-requests-only check
app.MapHangfireDashboardWithAuthorizationPolicy(
    Policies.PlatformAdmin,
    "/jobs",
    new DashboardOptions { Authorization = [], DisplayStorageConnectionString = false, DashboardTitle = "Jobs" });

app.Run();

static Task ApiAwareRedirect(RedirectContext<CookieAuthenticationOptions> context, int apiStatus)
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
    {
        context.Response.StatusCode = apiStatus;
    }
    else
    {
        context.Response.Redirect(context.RedirectUri);
    }

    return Task.CompletedTask;
}

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
