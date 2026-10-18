using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Application.Abstractions;
using Application.Common;
using Application.Merchants.Onboarding;
using Application.Notifications.SendEmails;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Notifications;
using Domain.Parcels;
using Infrastructure.MultiTenancy;
using Infrastructure.Persistence;

namespace Integration.Tests;

/// <summary>
/// What the integration tests share: visitors to a courier's site with or without a sign-in, a scope inside a courier,
/// and merchants and riders made fresh for a test, so tests running side by side never count each other's parcels.
/// </summary>
public abstract partial class AppTests(WebAppFactory factory)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    protected WebAppFactory Factory => factory;

    protected static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Today on the launch courier's clock (Dhaka), the day a parcel moved by a test is picked up on.</summary>
    protected static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Dhaka"));

    /// <summary>Runs SQL on the test database outside the app, as a data script does.</summary>
    protected static async Task Sql(string text)
    {
        await using var connection = new SqlConnection(WebAppFactory.ConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new SqlCommand(text, connection);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    /// <summary>
    /// A test about emails starts with none waiting. Every sign-up, approval and payout in every other test queues
    /// emails nobody sends, and the email job takes the oldest first, so a test's own email would sit behind them.
    /// </summary>
    protected static async Task ClearEmailBacklogAsync()
    {
        await Sql(
            $"UPDATE Notifications.OutboxMessage SET Status = {(byte)OutboxStatus.Sent} WHERE Status = {(byte)OutboxStatus.Pending} " +
            "AND Type IN (N'MerchantEmailMessage', N'CourierEmailMessage', N'RecipientEmailMessage', N'AccountEmailMessage')");
    }

    /// <summary>
    /// Runs the email job for courier <paramref name="slug"/> until nothing is due: it takes a batch at a time, and
    /// other tests queue emails meanwhile.
    /// </summary>
    protected async Task SendEmailsAsync(string slug)
    {
        for (var run = 0; run < 200; run++)
        {
            await using var scope = await ScopeAsync(slug);
            await scope.ServiceProvider.GetRequiredService<SendEmailsJob>().RunAsync(Cancel);
            var now = DateTime.UtcNow;
            var waiting = await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboxMessages.AsNoTracking()
                .AnyAsync(m => m.Status == OutboxStatus.Pending && (m.NextAttemptOn == null || m.NextAttemptOn <= now) &&
                    SendEmailsJob.Types.Contains(m.Type), Cancel);
            if (!waiting)
            {
                return;
            }
        }
    }

    /// <summary>A data script of this year, read from the repository, so a test proves the script that ships.</summary>
    protected static async Task<string> ScriptAsync(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Courier.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("Courier.sln not found above the test output.");

        return await File.ReadAllTextAsync(
            Path.Combine(root, "src", "Database Update", "Scripts", "2026", name),
            Cancel);
    }

    protected async Task<TenantInfo> TenantAsync(string slug)
    {
        return (await factory.Services.GetRequiredService<ITenantCatalog>().FindBySlugAsync(slug, Cancel))!;
    }

    /// <summary>A service scope inside the courier <paramref name="slug"/>, as a background job has.</summary>
    protected async Task<AsyncServiceScope> ScopeAsync(string slug)
    {
        var tenant = await TenantAsync(slug);
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);

        return scope;
    }

    protected async Task<T> QueryAsync<T>(string slug, Func<AppDbContext, Task<T>> query)
    {
        await using var scope = await ScopeAsync(slug);

        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected async Task<Parcel> ParcelAsync(string code, string slug = "onedrop")
    {
        return await QueryAsync(slug, db => db.Parcels.AsNoTracking().SingleAsync(p => p.TrackingCode == code, Cancel));
    }

    protected static string NewPhone()
    {
        return $"017{Random.Shared.Next(0, 100_000_000):D8}";
    }

    /// <summary>
    /// A new active merchant with a login, a default pickup point in <paramref name="area"/>, a bKash payout account and
    /// an API key, made for one test.
    /// </summary>
    protected async Task<TestMerchant> NewMerchantAsync(string slug = "onedrop", string area = "Mirpur 10", bool approved = true)
    {
        var name = $"Test shop {Guid.NewGuid():N}"[..20];
        var email = $"{Guid.NewGuid():N}@shop.test";
        var phone = NewPhone();
        await using var scope = await ScopeAsync(slug);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var areaId = await db.Areas.Where(a => a.Name == area).Select(a => a.Id).SingleAsync(Cancel);
        var created = await scope.ServiceProvider.GetRequiredService<MerchantOnboarding>().CreateAsync(
            new NewMerchant(new MerchantProfile(name, "Test Owner", phone, email, "Test road"), email, WebAppFactory.Password, areaId, "Test road, " + area),
            approved,
            Cancel);
        Assert.True(created.IsSuccess, created.Error?.Message);
        var merchant = await db.Merchants.SingleAsync(m => m.Id == created.Value, Cancel);
        merchant.SetPayoutAccount(PayoutMethod.Bkash, phone, "Test Owner");
        var (key, plaintext) = MerchantApiKey.Issue(merchant.Id, "Tests");
        db.MerchantApiKeys.Add(key);
        await db.SaveChangesAsync(Cancel);

        return new TestMerchant(merchant.Id, name, email, plaintext, slug);
    }

    /// <summary>A new rider of <paramref name="hubCode"/> on <paramref name="vehicle"/> with a login, made for one test.</summary>
    protected async Task<TestRider> NewRiderAsync(string hubCode, string slug = "onedrop", Vehicle vehicle = Vehicle.Motorbike)
    {
        var email = $"{Guid.NewGuid():N}@rider.test";
        await using var scope = await ScopeAsync(slug);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hubId = await db.Hubs.Where(h => h.Code == hubCode).Select(h => h.Id).SingleAsync(Cancel);
        var login = await scope.ServiceProvider.GetRequiredService<IUserAccounts>().CreateAsync(
            new NewLogin(email, WebAppFactory.Password, "Test rider", Roles.Rider),
            Cancel);
        var rider = Rider.Create(hubId, "Test rider " + email[..6], NewPhone(), vehicle, login.Value).Value;
        db.Riders.Add(rider);
        await db.SaveChangesAsync(Cancel);

        return new TestRider(rider.Id, email, hubCode);
    }

    /// <summary>Books a parcel through the API with the merchant's key and returns its tracking code.</summary>
    protected async Task<string> BookAsync(
        string apiKey,
        string area = "Gulshan 2",
        decimal cod = 1250,
        decimal weightKg = 0.5m,
        string? phone = null,
        string? reference = null)
    {
        var response = await factory.ClientFor(apiKey).PostAsJsonAsync(
            "/api/v1/parcels",
            new
            {
                MerchantReference = reference,
                RecipientName = "Rahim Uddin",
                RecipientPhone = phone ?? NewPhone(),
                RecipientAddress = "House 22, Road 4",
                Area = area,
                CodAmount = cod,
                WeightKg = weightKg,
                ItemDescription = "Panjabi"
            },
            Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Cancel)).GetProperty("trackingCode").GetString()!;
    }

    /// <summary>A visitor to one host ("" for the platform's), with <paramref name="cookie"/> as its sign-in.</summary>
    protected Visitor Visit(string slug, string cookie = "")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(slug.Length == 0 ? "http://localhost" : $"http://{slug}.localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        // An address of its own, so the per-address limits on public pages never count other tests' requests
        client.DefaultRequestHeaders.Add(ClientAddressFilter.Header, 
            $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");

        return new Visitor(client, cookie);
    }

    /// <summary>A visitor signed in as <paramref name="email"/> on <paramref name="slug"/>'s host.</summary>
    protected async Task<Visitor> SignInAsync(string slug, string email)
    {
        return Visit(slug, await SignInCookieAsync(slug, email));
    }

    protected async Task<string> SignInCookieAsync(string slug, string email, string? password = null)
    {
        var signedIn = await Visit(slug).PostFormAsync(
            "/Account/Login",
            "/Account/Login",
            ("Input.Email", email),
            ("Input.Password", password ?? WebAppFactory.Password));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

        return Visitor.Cookies(signedIn);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    protected sealed record TestMerchant(long Id, string Name, string Email, string ApiKey, string Slug);

    protected sealed record TestRider(long Id, string Email, string Hub);

    /// <summary>Sends its own cookies, and the page's anti-forgery cookie with a form, as a browser would.</summary>
    protected sealed class Visitor(HttpClient client, string cookie)
    {
        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content = null, string extra = "")
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            var cookies = string.Join("; ", new[] { cookie, extra }.Where(value => value.Length > 0));
            if (cookies.Length > 0)
            {
                request.Headers.Add("Cookie", cookies);
            }

            return client.SendAsync(request, Cancel);
        }

        public Task<HttpResponseMessage> GetAsync(string url)
        {
            return SendAsync(HttpMethod.Get, url);
        }

        /// <summary>The page's text, decoded (Razor encodes <c>+</c>, <c>=</c> and non-Latin letters).</summary>
        public async Task<string> PageAsync(string url)
        {
            var response = await GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)response.StatusCode}");

            return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancel));
        }

        /// <summary>Posts <paramref name="fields"/> to <paramref name="url"/> with the anti-forgery token of <paramref name="formPage"/>.</summary>
        public async Task<HttpResponseMessage> PostFormAsync(string formPage, string url, params (string Name, string Value)[] fields)
        {
            var form = await GetAsync(formPage);
            Assert.True(form.StatusCode == HttpStatusCode.OK, $"{formPage} answered {(int)form.StatusCode}");
            var token = Token().Match(await form.Content.ReadAsStringAsync(Cancel)).Groups[1].Value;
            var content = new FormUrlEncodedContent(
            [
                .. fields.Select(field => KeyValuePair.Create(field.Name, field.Value)),
                KeyValuePair.Create("__RequestVerificationToken", token)
            ]);

            return await SendAsync(HttpMethod.Post, url, content, Cookies(form));
        }

        /// <summary>Posts one file as a browser's file input would, with the anti-forgery token of <paramref name="formPage"/>.</summary>
        public async Task<HttpResponseMessage> UploadAsync(string formPage, string url, string field, byte[] file)
        {
            var form = await GetAsync(formPage);
            Assert.True(form.StatusCode == HttpStatusCode.OK, $"{formPage} answered {(int)form.StatusCode}");
            var token = Token().Match(await form.Content.ReadAsStringAsync(Cancel)).Groups[1].Value;
            var content = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
            content.Add(new ByteArrayContent(file), field, "picture.bin");

            return await SendAsync(HttpMethod.Post, url, content, Cookies(form));
        }

        /// <summary>Posts the form and follows the redirect, returning the page it lands on.</summary>
        public async Task<string> SubmitAsync(string formPage, string url, params (string Name, string Value)[] fields)
        {
            var posted = await PostFormAsync(formPage, url, fields);
            if (posted.StatusCode != HttpStatusCode.Redirect)
            {
                Assert.True(posted.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)posted.StatusCode}");

                return WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync(Cancel));
            }

            var next = new HttpRequestMessage(HttpMethod.Get, posted.Headers.Location);
            var cookies = string.Join("; ", new[] { cookie, Cookies(posted) }.Where(value => value.Length > 0));
            next.Headers.Add("Cookie", cookies);
            var landed = await client.SendAsync(next, Cancel);

            return WebUtility.HtmlDecode(await landed.Content.ReadAsStringAsync(Cancel));
        }

        public static string Cookies(HttpResponseMessage response)
        {
            return response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join("; ", cookies.Select(value => value.Split(';')[0]))
                : "";
        }
    }
}
