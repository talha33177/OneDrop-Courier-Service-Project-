# Putting OneDrop Courier live

For whoever hosts the site. One Linux server with Docker runs all of it: SQL Server, the database deployed from
nothing, the app, and Caddy in front of it for HTTPS. The files are in [`deploy/`](../deploy).

## What the live site starts with

- The courier (OneDrop Courier) with its 16 hubs, 21 zones, 87 areas and rate card, from the database scripts.
- One admin, named in `deploy/.env`.
- Nothing else: no demo logins, merchants, riders or parcels. Those exist only in Development; the live app runs as
  Production, where the demo seeder, the demo sign-in buttons and the `/Dev` pages are off.

What works for real, and what does not yet:

| | On the live site |
|---|---|
| Every panel, the merchant API, webhooks, public tracking | Real |
| Email to merchants (payout notices) | Real once a mail server is set in `.env`; otherwise nothing is sent |
| Merchants paying what they owe online | SSLCommerz, once a store is set in `.env`; a sandbox store takes test payments only |
| SMS to recipients | Not sent: there is no SMS provider yet |
| Payouts to merchants (bKash, Nagad, bank) | Worked out and invoiced, but no money moves: there is no payout provider yet |

## 1. A server

- **Linux on x86-64 (amd64).** SQL Server has no ARM image, so not an ARM server (Hetzner CAX, Oracle Ampere, AWS
  Graviton).
- **4 GB of memory** (SQL Server needs 2 GB on its own), 2 CPUs, 30 GB of disk. The commands below assume Ubuntu 24.04.
- **Ports 80 and 443** open to the internet (Caddy needs 80 to get the certificate).
- **Docker:** `curl -fsSL https://get.docker.com | sh`

## 2. An address

`SITE_ADDRESS` is the name people type, without `https://`. Either:

- a domain or subdomain you control, with an **A record** pointing at the server's IP (`courier.example.com`); or
- no domain at all: **`<ip-with-dashes>.sslip.io`**, for example `203-0-113-5.sslip.io` for 203.0.113.5. It points at
  that IP by itself, and HTTPS works on it too.

Caddy fetches the HTTPS certificate on the first start, once the name points at the server, and renews it itself.

## 3. The code and the settings

```bash
git clone https://github.com/talha33177net-eng/OneDrop.git onedrop   # private: needs access or a deploy key
cd onedrop/deploy
cp .env.example .env
nano .env
```

| Setting | |
|---|---|
| `SITE_ADDRESS` | The address from step 2 |
| `SA_PASSWORD` | SQL Server's `sa` password: at least 8 characters from three of upper case, lower case, digits, symbols. **Set once**: SQL Server takes it only when its data volume is first created, so changing it later locks the app out |
| `ADMIN_EMAIL`, `ADMIN_NAME`, `ADMIN_PASSWORD` | The courier's admin, made on the first start. The password needs at least 8 characters with an upper case letter, a lower case letter and a digit |
| `EMAIL_*` | Optional. A mail server for merchants' payout emails (Gmail: `smtp.gmail.com`, port 587, an app password) |
| `SSLCOMMERZ_*` | Optional. An SSLCommerz store; the sandbox (`https://sandbox.sslcommerz.com/`) until it goes live (`https://securepay.sslcommerz.com/`) |
| `GOOGLE_MAPS_API_KEY` | Optional. A Google Maps browser key for the booking form's map and the parcel pages' small maps. In Google Cloud turn on Maps JavaScript API, Places API (New), Geocoding API and Maps Embed API, and restrict the key to `https://<SITE_ADDRESS>/*`. Without it merchants paste a Google Maps link and riders still get directions |

Keep `$`, `;` and quotes out of the passwords: they go into connection strings, and compose reads `$` as a variable.
`.env` is git-ignored.

## 4. Start it

```bash
docker compose up -d --build        # the first build takes a few minutes
docker compose logs -f web          # wait for "Setup: created the admin" and "Application started"
```

The `database` service runs first and exits once the database is ready (`docker compose ps -a` shows it as
`Exited (0)`); the app starts after it.

Then open `https://SITE_ADDRESS`, sign in with the admin's email and password, and:

1. Change the password (the account menu at the top right, **Change password**).
2. Empty `ADMIN_PASSWORD` in `.env` and run `docker compose up -d`. The admin stays: a start never changes an admin
   that exists, it only makes one that is missing.
3. Follow the setup checklist on the admin's dashboard: add riders (each gets a login), then approve the merchants who
   sign up on the site, or add them yourself. The map and the rate card are already there; **How it works** in the menu
   explains the rest.

## Updating

```bash
cd ~/onedrop && git pull
cd deploy && docker compose up -d --build
```

The `database` service applies any new schema and data scripts before the new app starts. Sign-ins survive an update
(the `keys` volume).

## Backups

The data lives in the `sql-data` volume. A backup file, copied out of the container:

```bash
docker compose exec -T sql bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b' <<'SQL'
BACKUP DATABASE [OneDrop] TO DISK = N'/var/opt/mssql/data/OneDrop.bak' WITH INIT;
SQL
docker compose cp sql:/var/opt/mssql/data/OneDrop.bak ./OneDrop-$(date +%F).bak
```

`docker compose down` stops everything and keeps the data; `docker compose down -v` **deletes** the database, the
certificates and the sign-in keys.

## Hosting it another way

The app is an ordinary ASP.NET Core 10 app on SQL Server, so any host that runs a container (the `web` target of the
[Dockerfile](../Dockerfile)) or .NET 10 and can reach a SQL Server works, Azure App Service with Azure SQL among them.
What it needs:

- **The database deployed before the app starts, and again on every update:**
  `pwsh tools/db/publish.ps1 -ConnectionString "<connection string>"` (needs the .NET 10 SDK, PowerShell and
  `dotnet tool install -g microsoft.sqlpackage`). The schema targets Azure SQL, so it publishes there as well as to
  SQL Server 2022 or later.
- **Exactly one instance.** The background jobs (payouts, texts, emails, webhooks) and the live dashboards run inside
  the app.
- **The sign-in keys kept** across restarts: `/home/app/.aspnet/DataProtection-Keys` in the container on storage that
  survives, or everyone is signed out on every deploy (Azure App Service keeps them by itself).
- **The settings**, as environment variables:

| Variable | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` behind any proxy or load balancer that ends HTTPS (nearly every host), so the app sees https and the visitor's address |
| `ConnectionStrings__Database` | `Server=…;Database=OneDrop;User Id=…;Password=…;TrustServerCertificate=true;MultipleActiveResultSets=true` (`MultipleActiveResultSets=true` is required) |
| `Tenancy__HomeCourierHost` | The site's address, e.g. `courier.example.com` or the host's own `myapp.azurewebsites.net` |
| `Links__PortalUrlFormat` | `https://<the same address>/`, for links in texts and emails, and SSLCommerz's return address |
| `Setup__AdminEmail`, `Setup__AdminName`, `Setup__AdminPassword` | The first admin, as above |
| `Email__Host`, `Email__Port`, `Email__User`, `Email__Password`, `Email__From`, `Email__FromName` | Optional mail server |
| `Sslcommerz__StoreId`, `Sslcommerz__StorePassword`, `Sslcommerz__BaseUrl` | Optional SSLCommerz store |
| `GoogleMaps__ApiKey` | Optional Google Maps browser key, as above |

## Several couriers (optional)

The app is multi-tenant: a courier can also live at `{slug}.{root domain}`. For that, leave
`Tenancy__HomeCourierHost` empty, set `Tenancy__RootDomain` to the domain and `Links__PortalUrlFormat` to
`https://{slug}.<domain>/`, point the domain and each courier's subdomain at the server, and list those names in the
Caddyfile. A visitor to the bare domain is sent to the courier in `Tenancy__HomeCourier` (`onedrop`). A new courier
is added with a data script like `src/Database Update/Scripts/2026/001_SeedCourier.sql`.

## When something goes wrong

| What you see | Why, and what to do |
|---|---|
| The browser says the connection is not secure, or `docker compose logs caddy` shows a failed challenge | The address does not point at the server yet, or port 80 is closed. Fix DNS or the firewall; Caddy keeps trying |
| `web` stops at start with "Setup: could not create the admin" | The admin's password does not meet the rules above |
| `database` exits with an error | `docker compose logs database`. After the first start this is usually `SA_PASSWORD` changed in `.env`: put the first one back |
| Everyone was signed out after an update | The `keys` volume was removed |
| Anything else | `docker compose logs web`; every request and error is logged there |
