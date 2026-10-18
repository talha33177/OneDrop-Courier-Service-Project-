# Development conventions

Courier platform for online shops (pickup, hubs, delivery, cash on delivery, next-day payouts). Multi-tenant
modular monolith on .NET 10, SQL Server. The product is described in `README.md`, the plan and progress in
`Plans/Implementation-Plan.md`.

[Project-Context.md](Project-Context.md) explains the product, the decisions and the environment.
[Database.md](Database.md) is the authority for SQL, the SQL project and DbUp. It is not repeated here.

## Working rhythm

- **Start of the day:** read `Plans/Implementation-Plan.md` ("Today" section) and continue with the first
  unchecked task of the current week.
- **After every task:** run the plan's test routine — build, new tests for the new work, republish both databases
  if the schema changed, all three test suites green (integration must report *succeeded*, not *skipped*), and a
  live check in the running app. A task is not done until this passes.
- **Then:** tick the task in the plan, update "Today", and add the day's entry to the daily log with what was
  tested.

## Build and test

```bash
dotnet build Courier.sln
dotnet test --project tests/Domain.Tests
dotnet test --project tests/Architecture.Tests
dotnet test --project tests/Integration.Tests
```

- Tests run on Microsoft.Testing.Platform (`global.json`), so use `dotnet test --project <path>`.
- The integration suite runs against `OneDrop-Test` on ras-x2, named in the git-ignored
  `tests/Integration.Tests/testsettings.Local.json` (`INTEGRATION_TEST_DB` overrides it). With no database
  configured it skips itself, and a green run with everything skipped means nothing ran. After a schema change,
  update it with `./tools/db/publish.ps1 -Database OneDrop-Test`. Ask before creating any other database on ras-x2.
- Never point integration tests at the dev database `OneDrop`.
- Connection strings, the mail password and the SSLCommerz keys (they are secrets) live only in git-ignored
  `*.Local.json` files. Never commit one. The live server's copy is sent to the owner's teammate by hand.
- No product-name prefix in code: projects, folders and namespaces are `Domain`, `Application`, `Infrastructure`,
  `Web`, `Database`, `Database Update` (like DCN). The OneDrop brand appears only in user-facing text and data.

## Architecture

- Domain → nothing. Application → Domain (+ EF Core for DbSet/LINQ only). Infrastructure → Application.
  Web → Infrastructure. Business rules never reference Infrastructure or Web.
- Use cases are vertical slices in `src/Application/<Module>/<UseCase>/`: command, validator, handler.
  Handlers are plain scoped classes registered in `Application/DependencyInjection.cs`; there is no mediator.
- A business "no" is a `Result` with an `Error` (code, message, type); exceptions are for bugs. The API maps
  errors to problem details in `Web/Api/ResultMapping.cs`.
- Aggregates guard their own rules: `Parcel.Create` validates, `Parcel` moves only along its status state machine.
  Methods with five or more parameters take a record (`NewParcel`).
- Side effects of a change (SMS, later webhooks) never run inside the use case: the entity raises a domain event
  (`Raise`), the save writes it to the outbox in the same transaction, and `SendOutboxJob` acts on it later. A new
  event needs a contract and a case in `Application/Notifications/OutboxContracts`.

## Tenancy (read before touching data access)

- Every entity outside `Domain/Platform` derives from `TenantEntity`. `Architecture.Tests` fails otherwise.
- `TenantId`, `Created`, `UpdatedOn`, `UpdatedId` are stamped by `TenantSaveInterceptor`; never set them in code.
- Queries are filtered automatically (`AppDbContext` named filters `Tenant` and `Merchant`). Do not add
  `IgnoreQueryFilters()` without a filter key, and only where crossing tenants is the point (API key lookup,
  platform pages). Say why in a comment.
- The database enforces the tenant too (Row-Level Security, `Platform.TenantIsolation`): every connection the
  context opens carries its tenant (`TenantSessionInterceptor`), so a lifted tenant filter, `ExecuteUpdate` or raw
  SQL still reaches only the current tenant's rows. A read that must cross tenants also wraps itself in
  `await using (await db.AcrossTenantsAsync())`, kept as short as the read.
- A request's tenant comes from the subdomain (`TenantResolutionMiddleware`) or the API key
  (`ApiKeyAuthenticationHandler`). Background work sets `TenantContext` from a job parameter.
- A merchant must only ever see its own orders; another tenant's or merchant's row is a 404, never a 403.

## C# conventions

`.editorconfig` covers formatting (it is DCN's). House rules it cannot express:

- Latest C#: `var`, collection expressions, primary constructors, pattern matching, file-scoped namespaces.
- Braces always, opening brace on its own line, blank line before the final `return`, wrap near 120 columns.
- No single-use locals and no one-line private helpers. Comment only what the code cannot say.
- Money is `decimal`, stored `DECIMAL(12,2)` (fees `DECIMAL(10,2)`). Times are UTC `DateTime`.
- Phones go through `PhoneNumber.Parse` and are stored E.164 (`+8801XXXXXXXXX`).

## User-facing text

- Sentence case everywhere ("All parcels", never "All Parcels"). "and", never "&".
- Currency is ৳ with no decimals in the UI (`৳110`).
- Never show a raw id; show a name or the order number.

## Look

- Colour is ink on cool light grey paper with one accent, sky blue (`--accent`), for the logo, the main call to action
  and what is selected or live; text in the accent uses the deeper `--accent-text`. Status colours are for statuses
  only. Flat fills: no gradients, glows or violet. Pages use the tokens in `site.css`, never a colour of their own.
- The logo is `<partial name="_BrandMark" />` in pages; `wwwroot/images/logo.svg` (and `logo-on-dark.svg`) for
  documents. Its text is outlined, so it needs no font.
- Drawings are line art (ink outlines, paper and sky blue fills) in `Pages/Shared/Art`, animated from `landing.css`.
  They carry `data-motion` so they rest while off screen, and stay still for reduced-motion users.

## Working in this repo

- The repo is CRLF (`.gitattributes`). Scope bulk `sed` edits to the files you mean.
- Demo data (logins, merchants, API keys) is Development-only, created by `DemoDataSeeder`.
