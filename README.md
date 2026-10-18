# OneDrop Courier

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/Web/wwwroot/images/logo-on-dark.svg" />
  <img src="src/Web/wwwroot/images/logo.svg" alt="OneDrop courier" width="260" />
</picture>

**OneDrop Courier: doorstep delivery and cash on delivery across Bangladesh.** A courier service for online shops, in
the way Steadfast, Pathao or RedX work: a merchant books parcels, a rider picks them up, the hubs sort them, a rider
delivers each one and collects the cash on delivery, and the merchant is paid that cash, less the courier's charges,
the next day.

.NET 10 · ASP.NET Core (Razor Pages + REST API) · EF Core 10 · SQL Server · Clean Architecture modular monolith.

| Who | What they do |
|---|---|
| Merchant | Signs up (approved by the courier), books parcels by form, spreadsheet or API, asks for pickups, prints labels, follows every parcel (by stage, by date, in stats over any period, and a bell of what happened), asks the courier to cancel a parcel on its way or change its cash, confirms the returns a rider brings back, checks a phone number's delivery record, keeps several payout accounts and asks to be paid early, sees its payments and invoices, exports its parcels, and adds moderators who work in the account with a sign-in of their own |
| Hub staff | Each has a job at one hub (hub manager, receiving, line-haul, delivery desk, returns desk, cash desk) and works only its desks there: scan parcels in and out (receive, send to the delivery hub, hand back returns), send riders to pickups, hand parcels to riders, send returns back to merchants with a rider, flag a parcel with a problem, close each rider's run with the cash handed in. A hub manager adds the hub's own staff |
| Rider | A phone screen: today's pickups, deliveries and returns to hand back, and at each door delivered, partly delivered, on hold or refused |
| Courier admin | A super admin does everything; head office staff (operations, accounts, merchant support, customer support, relief) open only the panels their job opens. Live dashboard, merchants (approve, suspend, edit), merchants' requests to answer, riders, the rate card, coverage, payouts, reports, failed messages |
| Recipient | Public tracking by code, and an SMS when the parcel goes out and when it is delivered |

Prices come from the courier's rate card, per service area (seeded values):

| Service area | Up to 1 kg | Each extra kg | COD charge | Extra for a return |
|---|---|---|---|---|
| Inside city | ৳60 | ৳15 | 1% | ৳0 |
| Suburb | ৳100 | ৳20 | 1% | ৳50 |
| Outside city | ৳120 | ৳20 | 1% | ৳60 |

## Documents

| Read | For |
|---|---|
| [Documentation/Project-Context.md](Documentation/Project-Context.md) | The product, every decision and why, the environment, traps already found. Start here |
| [Documentation/Architecture.md](Documentation/Architecture.md) | Diagrams: layers, the life of a parcel, parcel states, tenant isolation, the outbox, a day at the courier |
| [Documentation/Demo.md](Documentation/Demo.md) | The demo script, step by step |
| [Documentation/Deployment.md](Documentation/Deployment.md) | Putting it live on a server: a clean start with one admin, HTTPS, updates and backups |
| [Plans/Implementation-Plan.md](Plans/Implementation-Plan.md) | What is built, how it was tested, what is next, the decisions log and the daily log |
| [Documentation/Conventions.md](Documentation/Conventions.md), [Documentation/Database.md](Documentation/Database.md) | Rules for code and SQL |

## Run it

Prerequisites: .NET 10 SDK, SQL Server (the dev server is `ras-x2,1433`), and SqlPackage
(`dotnet tool install -g microsoft.sqlpackage`).

**One-time setup: connection strings.** They contain the database password, so they are kept in git-ignored
files that you create on each machine:

| File | Database |
|---|---|
| `src/Web/appsettings.Local.json` | Development database (`OneDrop`) — used by the app and by `publish.ps1` |
| `tests/Integration.Tests/testsettings.Local.json` | Test database (`OneDrop-Test`) — used by the integration tests |

Both have the same shape:

```json
{
  "ConnectionStrings": {
    "Database": "Server=ras-x2,1433;Database=OneDrop;User Id=...;Password=...;TrustServerCertificate=true;MultipleActiveResultSets=true"
  }
}
```

**Email and online payments are set in the admin panel.** A super admin enters the courier's mail server and its
SSLCommerz store (sandbox or live) on **Setup → Email and payments** (`/Admin/Connections`); the passwords are kept
encrypted and never shown again, and a button sends a test email. That is all a live site needs. What follows is the
fallback for a courier that has set neither, for example on a developer's machine.

**Sending email (optional).** Merchants are emailed when a payout leaves. Add the mail server to the same git-ignored
`src/Web/appsettings.Local.json`; with no `Email:Host` the app keeps the emails in memory and lists them at
`/Dev/Emails`, which also has a button that sends a test email.

```json
{
  "Email": {
    "Host": "smtp.gmail.com",
    "Port": 587,
    "User": "you@gmail.com",
    "Password": "a Google app password, not the account password",
    "From": "you@gmail.com",
    "FromName": "OneDrop Courier"
  }
}
```

The integration tests always use the fake sender, whatever is configured here.

**Taking payments online (optional).** A merchant who owes the courier (its charges came to more than the cash
collected for it) can pay it online through SSLCommerz from its Payments page. Add the store to the same git-ignored
file; with no store, Development uses a test gateway whose payment page is `/Dev/Pay/…`, and any other environment
offers no online payment.

```json
{
  "Sslcommerz": {
    "StoreId": "your sandbox store id",
    "StorePassword": "your sandbox store password",
    "BaseUrl": "https://sandbox.sslcommerz.com/"
  }
}
```

`BaseUrl` is the sandbox until the store goes live (`https://securepay.sslcommerz.com/`). SSLCommerz sends the payer
back to `/pay/{transaction}/success` on the courier's own address, which works on localhost; its server-to-server
notice (`/pay/notice`) cannot reach localhost, so there a payment whose browser never came back is found by the check
job that runs every 10 minutes. The integration tests always use the test gateway.

**Google Maps (optional).** Merchants pin the recipient's door on a map while booking, and riders tap Navigate to get
Google Maps directions to it. The map in the booking form, its place search and the small maps on parcel pages need a
Google Maps browser key with Maps JavaScript API, Places API (New), Geocoding API and Maps Embed API turned on; put it
in the same git-ignored file as `"GoogleMaps": { "ApiKey": "…" }` and restrict it to your address in Google Cloud.
Without a key, merchants paste the Google Maps link the customer sent (short `maps.app.goo.gl` links are opened by
the server) and riders still get directions. The integration tests never use a key.

Then:

```powershell
./tools/db/publish.ps1                      # dbup pre -> publish the dacpac -> dbup (the courier, hubs, zones, areas, rates)
dotnet run --project src/Web                # the app; demo logins only with Seed:DemoData (below)
```

| Address | What |
|---|---|
| http://onedrop.localhost:5080 | OneDrop Courier: public site, tracking, sign-up, and every panel |
| http://localhost:5080 | Sends visitors to OneDrop Courier (`Tenancy:HomeCourier`); platform admins sign in at http://localhost:5080/Account/Login and land on the list of couriers |

Browsers resolve `*.localhost` to your machine, so no hosts-file changes are needed.

### Fill it with more parcels

With the app running, the simulator makes up shops and books their parcels through the API:

```powershell
dotnet run --project tools/Simulator -- --parcels 40              # 40 parcels from 6 made-up shops
dotnet run --project tools/Simulator -- --parcels 40 --pace 2     # one every 2 seconds: watch /Admin fill up
```

`--shops` (up to 8), `--seed`, `--tenant` (default: every courier in the database) and `--url` (default http://localhost:5080) are optional.
Each run gives each shop a new API key.

### Run it in Docker

No SQL Server or SDK needed, only Docker:

```powershell
copy .env.example .env          # then choose a SQL Server password in .env (git-ignored)
docker compose up --build       # SQL Server 2025, the database deployed from nothing, then the app
```

The same address works (http://onedrop.localhost:5080), with the demo logins below and sample parcels. SQL Server is on
`localhost,14330`; `docker compose down -v` deletes its data. This is a demo on your own machine; a live site uses
`deploy/` instead ([Deployment.md](Documentation/Deployment.md)).

### Demo logins (Development only, password `OneDrop#2026`)

Made on start when `Seed:DemoData` is true, which is off by default: turn it on in your git-ignored
`src/Web/appsettings.Local.json` (`"Seed": { "DemoData": true, "DemoActivity": true }`; `DemoActivity` also books
about twenty sample parcels in every stage). The sign-in page then lists them as one-press buttons. Outside Development
there are no demo logins at all: a live site starts with only the admin named when it is deployed
([Deployment.md](Documentation/Deployment.md)).

| Login | Role |
|---|---|
| `admin@onedrop.test` | Super admin |
| `hub@onedrop.test` | Relief staff: any desk at any hub (the hub is picked at the top of each hub page) |
| `mirpur@onedrop.test`, `receiving@onedrop.test` | Mirpur hub manager (every desk there and the hub's staff) and a Mirpur receiving clerk |
| `accounts@onedrop.test` | Accounts at head office: payouts, adjustments, online payments, reports |
| `fashion@onedrop.test`, `gadget@onedrop.test`, `beauty@onedrop.test`, `crafts@onedrop.test` | Merchants (Fashion House, Gadget BD, Beauty Shop, Chattogram Crafts) |
| `organic@onedrop.test` | Organic Bazar, a merchant still waiting for approval |
| `rider@onedrop.test`, `rider2@` (Mirpur), `rider3@` (Gulshan), `rider4@` (Chattogram), `rider5@` (Dhanmondi), `rider6@` (Uttara) | Riders |
| `admin@platform.test` on http://localhost:5080/Account/Login | Platform admin |

The **Demo mode** bar links the fake gateways: **SMS inbox** (`/Dev/Sms`), **Payouts sent** (`/Dev/Payouts`) and
**Webhooks received** (`/Dev/Webhooks`). Without an SSLCommerz store, a merchant's "Pay online" opens the test
payment page (`/Dev/Pay/…`), which pays, fails, cancels, or pays a payment marked risky.

### Demo API keys (Development only)

| Merchant | Key |
|---|---|
| Fashion House | `od_odfashion001_DevOnlyKeyDoNotUseInProduction01` |
| Gadget BD | `od_odgadget0001_DevOnlyKeyDoNotUseInProduction02` |
| Beauty Shop | `od_odbeauty0001_DevOnlyKeyDoNotUseInProduction03` |
| Chattogram Crafts | `od_odctgcraft01_DevOnlyKeyDoNotUseInProduction04` |

Merchants make and revoke their own keys on **API keys**.

## The merchant API

```bash
curl http://onedrop.localhost:5080/api/v1/parcels \
  -H "X-Api-Key: od_odfashion001_DevOnlyKeyDoNotUseInProduction01" \
  -H "Idempotency-Key: INV-1001" \
  -H "Content-Type: application/json" \
  -d '{
        "merchantReference": "INV-1001",
        "recipientName": "Rahim Uddin",
        "recipientPhone": "01712-345678",
        "recipientAddress": "House 12, Road 5",
        "area": "Gulshan 2",
        "codAmount": 1250,
        "weightKg": 1.5,
        "itemDescription": "Panjabi"
      }'
```

| Endpoint | |
|---|---|
| `POST /api/v1/parcels` | 201 with the tracking code (`OD10000001`), status, service area, delivery hub and charges. A retry with the same `Idempotency-Key` returns 200 and the same parcel; a different body with that key 409. A merchant waiting for approval gets 403 |
| `GET /api/v1/parcels/{code}` | The caller's own parcel and its history, and any problem the courier flagged on it (`issue`: `inReview` or `exceptional`, with `issueNote`); anyone else's is 404 |
| `POST /api/v1/parcels/{code}/cancel` | Before pickup only (409 after); optional `reason` |
| `GET /api/v1/charge?area=&weightKg=&codAmount=` | What a parcel would cost: service area, delivery charge, COD charge, total, return charge |
| `GET /api/v1/areas` | The areas an address must pick from, with their city and hub |

### Status changes by webhook

A merchant sets a webhook address on **Webhooks** (`/Merchant/Webhook`, https only; plain http only to localhost) and
gets a `whsec_` secret there. Each status change of its parcels is posted through the outbox:

```json
{ "type": "parcel.status_changed", "timestamp": "2026-10-04T05:38:12Z",
  "data": { "trackingCode": "OD10000008", "merchantReference": "INV-2007", "status": "outForDelivery",
            "codAmount": 1250, "collectedAmount": null, "deliveryCharge": 60, "reason": null } }
```

Signed as [Standard Webhooks](https://www.standardwebhooks.com/): headers `webhook-id` (the same on a retry),
`webhook-timestamp` and `webhook-signature` = `v1,` + base64 HMAC-SHA256 of `{id}.{timestamp}.{body}` keyed with the
base64-decoded secret. Any 2xx answer counts; otherwise it is retried after 1, 2, 4 and 8 minutes, then given up
(the admin can send it again from **Failed messages**).

Background jobs run on Hangfire in the web app (the hourly merchant payouts); the dashboard is at
http://localhost:5080/jobs (platform admin).

## Layout

```
src/
  Domain            entities and business rules, no packages (Parcels, Pricing, Delivery, Payments, Merchants, Network)
  Application       one folder per use case (Parcels/CreateParcel, Hubs/HubScan, Delivery/RiderDay, Payments/RunPayouts, ...)
  Infrastructure    EF Core mapping, tenancy, Identity, Hangfire, fake SMS and payout gateways, webhooks, demo seeder
  Web               Razor Pages panels, /api/v1, SignalR, middleware, composition root
  Database          SQL project: owns the schema, builds a dacpac
  Database Update   DbUp data migrations (dbup.exe)
tests/
  Domain.Tests          business rules
  Architecture.Tests    every entity has a TenantId; the SQL project lists every .sql file
  Integration.Tests     real database: API, panels, the delivery flow, payouts, webhooks, isolation sweep, Row-Level Security
tools/
  db/publish.ps1        deploy the database (Windows PowerShell and PowerShell 7 on Linux)
  Simulator             demo shops and parcels through the running app's API
Dockerfile, docker-compose.yml, .github/workflows/ci.yml
deploy/                 the live site: production compose (SQL Server, the app, Caddy for HTTPS) and its settings
Documentation/          context, architecture diagrams, demo, conventions
Plans/                  the plan and daily log
```

## Database

The schema is owned by the SQL project, **not** by EF migrations — the same setup as DCN. A new table or column goes
into `src/Database/<Schema>/Tables/<Table>.sql` first, then into the EF mapping in
`src/Infrastructure/Persistence/Configurations`. Data changes go into DbUp scripts. The integration test
`SchemaMatchesModelTests` fails if the two disagree. Full rules: [Documentation/Database.md](Documentation/Database.md).

## Keeping couriers and merchants apart

The system is multi-tenant: a tenant is a courier operator with its own hubs, riders, merchants and rates. One
courier is seeded; the integration tests add a second ("rival") to prove the walls hold.

1. **EF Core query filters** — every tenant-owned entity gets `WHERE TenantId = @current`, and merchant-owned ones
   also `WHERE MerchantId = @merchant` for merchant callers. No tenant set means no rows.
2. **Save interceptor** — stamps `TenantId` on new rows and refuses writes to another tenant's rows.
3. **SQL Server Row-Level Security** — the policy `Platform.TenantIsolation` filters every table with a `TenantId` by
   the tenant each application connection names in `SESSION_CONTEXT`, and refuses rows written for another tenant.

The tenant comes from the subdomain (panels) or the API key (merchant API). A merchant sees only its own parcels, and
a rider only the parcels given to them.

## Tests

```powershell
dotnet test --project tests/Domain.Tests
dotnet test --project tests/Architecture.Tests

./tools/db/publish.ps1 -Database OneDrop-Test   # after any schema change: keeps the test database current
dotnet test --project tests/Integration.Tests    # uses OneDrop-Test from testsettings.Local.json
```

The integration tests use their own database, `OneDrop-Test`, so their parcels never reach the development database
`OneDrop`. Set `INTEGRATION_TEST_DB` to point them somewhere else (CI, another developer's database).

**CI** (`.github/workflows/ci.yml`): builds, starts a throwaway SQL Server 2025 with a password made up for the run,
deploys the database into it from nothing with `publish.ps1`, runs the three suites (failing if the integration tests
were skipped) and builds the Docker images.
