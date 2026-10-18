# Project context — read this first

Everything someone new to this project needs to continue the work without asking: what we are building and why,
what exists, every decision taken and the reason for it, the environment, and the traps already found.

**Reading order for a new session:**
1. This file.
2. [Plans/Implementation-Plan.md](../Plans/Implementation-Plan.md) — the "Today" section says where we stopped and
   what is next; the daily log says what was done and tested.
3. [Conventions.md](Conventions.md) and [Database.md](Database.md) — the rules code and SQL must follow.
4. [Architecture.md](Architecture.md) for diagrams of how the parts fit, and [Demo.md](Demo.md) for the demo.

Keep this file current: when a decision, a working rule or the environment changes, update it in the same change.

---

## 1. The product in one page

**OneDrop Courier** is a courier service for online shops in Bangladesh, built the way the country's couriers work
(Steadfast, Pathao, RedX, Paperfly): a shop books a parcel, a rider picks it up, the hubs sort it, a rider delivers it
and collects the cash on delivery (COD), and the shop is paid that cash, less the courier's charges, the next day.

> **The pivot (2026-10-04).** Until then the project was "OneDrop — multiple shops, one delivery": orders from several
> shops for one customer were grouped into one delivery over three days. The owner dropped that idea: "forget the
> group delivery system … make this project just like the traditional delivery systems of Bangladesh like Steadfast".
> All grouping logic, customer accounts, trips and shelves were removed, both databases were reset and the schema and
> seed were written anew. The last commit of the grouping product is `3b48bc9`; an uncommitted rewrite of its Week 5
> screens is kept as `git stash@{0}`. Its plan and daily log are in git history, not in the current plan.

### Who uses it
- **Merchant** (a shop: website, Facebook page): signs up on the public site and waits for the courier's approval;
  books parcels by form, CSV upload or API; asks for pickups; prints QR labels; follows every parcel; checks a phone
  number's record before sending COD (fraud check); sees what it is owed, its payouts and invoices; manages API keys
  and a webhook.
- **Hub staff** (2026-10-12): each has a job at one hub and works only its desks there: receiving (scan in, weigh,
  flag), line-haul (scan out to another hub), delivery desk (pickups, assigning parcels to riders, run sheets),
  returns desk (return lists, hand back), cash desk (close each rider's run with the cash handed in), or hub manager
  (every desk, and the hub's own staff logins). Relief staff at head office cover any desk at any hub, the hub picked
  on the page.
- **Head office staff** (2026-10-12): operations manager (every hub's work, the dashboard, stuck parcels, riders,
  requests), accounts (payouts, adjustments, online payments, reports), merchant support (sign-ups, merchants,
  requests), customer support (parcel lookup only). The **super admin** does everything.
- **Rider**: a phone screen (installable) with today's pickups and deliveries; records each door as delivered, partly
  delivered, on hold (reason and a date) or refused.
- **Courier admin**: live dashboard, merchants (approve, suspend, edit, payout account), riders, the rate card,
  coverage (hubs, zones, areas), payouts (run now, invoices), failed messages.
- **Recipient**: no account. Public tracking by code, and an SMS when the parcel goes out and when it is delivered.

### Pricing (the courier's rate card, seeded)
| Service area | Up to 1 kg | Each started kg above | COD charge | Extra for a return |
|---|---|---|---|---|
| Inside city | ৳60 | ৳15 | 1% | ৳0 |
| Suburb | ৳100 | ৳20 | 1% | ৳50 |
| Outside city | ৳120 | ৳20 | 1% | ৳60 |

On top come the **delivery types** (2026-10-12; the admin's own since 2026-10-13): a speed and what is inside, each an
extra on the delivery charge per service area (none means not offered there), made, priced, ordered and taken off by
the courier's admin on `/Admin/DeliveryTypes` (`Pricing.DeliveryOption`). OneDrop Courier starts with regular and
general goods (the defaults: everywhere, no extra), **express** ৳40 (delivered the day after pickup, inside city and
suburb only) and liquid ৳10, fragile ৳20, fresh food ৳30, frozen ৳50 (fresh food and frozen inside city and suburb
only). An option marked "goes first" (express, food that spoils) leads at the hub and on the rider's run; labels, hub
lists and the rider's app show the tags and the contents' handling note.

The service area comes from the pickup point's zone and the destination's zone (`ServiceAreas.Between`): same city
and neither a suburb → inside city; same city with a suburb zone → suburb; different cities → outside city. The COD
charge is a percentage of the COD amount rounded to whole taka (away from zero). A returned parcel costs the delivery
charge plus the return charge; a cancelled one costs nothing; a delivered or partly delivered one costs the delivery
charge plus the COD charge on what was collected. A hub that weighs a parcel (`MeasuredWeightGrams`) reprices the
delivery charge on its scale, at the rates the parcel was booked under; the merchant's own figure is kept and both are
shown on the parcel.

### The life of a parcel
**One visit a day to each pickup point** (2026-10-12; the cut-off went on 2026-10-13): the pickup van leaves the hub at
`Tenant.PickupTime` (3:30 pm) and visits each merchant's pickup point once. A parcel booked before the van reaches the
point goes with that day's pickup, however late (a minute before the rider is at the door counts); once the van has been
there (a `Completed` pickup made today, `PickupVisits.MadeToday`) a parcel booked at that point goes with the next day's
pickup, which is listed at once. There is no cut-off time and no setting for one. Booking puts the pickup point on the
day's list (`Delivery.PickupRequest`, one open per point and day); merchants never ask for a pickup. The rider collects
every parcel waiting at the point that they tick, and what they leave behind goes with tomorrow's pickup. The rider's
form posts every code it offered them (`shown`), so a parcel booked while they stood at the door does not slip past:
the pickup is refused with "the merchant booked n more parcels while you were here" and the list is drawn again with it
on it (`pickup.booked`), because that parcel is the one the owner's rule says they take. A pickup dated after today
cannot be given to a rider or collected (`PickupsHandler.NotYet`). The `pickups` job (every 15 minutes)
carries a missed pickup to today (or tomorrow when the van has been to its point today), lists points whose parcels
wait without a pickup, and calls off pickups with nothing left to collect. A booking made after the van left but at a
point with no pickup yet today joins today's list for the hub to send the van to; if nobody collects it, the job
carries it to the next day.

**Only the pickup van goes to a merchant** (2026-10-13): a bicycle or a motorbike takes parcels to recipients and is
never sent to collect (`Vehicles.CollectsPickups`, true for the van alone), so every hub needs at least one rider on a
pickup van. A collected parcel stays on the van (`Parcel.RiderId` while `PickedUp`) until the hub scans it in, so what
the van holds is known: the hub is given pickups only while the van carries them *together with* what is already on it
and what it is on its way to collect (`RiderLoads`). The round takes the pickups oldest first until the van is full and
says how many stayed behind; one pickup bigger than the whole van is allowed onto an empty van, which makes more than
one trip. The same load is what limits a delivery run and a return list, counted at the weight the hub's scale read
(`Parcel.BilledWeightGrams`), not the merchant's figure.

`Pending` (booked) → `PickedUp` (rider collected) → `AtHub` (received) → `InTransit` (sent to the delivery hub) →
`AtHub` → `OutForDelivery` (in a rider's run) → `Delivered` / `PartlyDelivered` / `OnHold` / `Returning` → `Returned`
(handed back at the pickup hub). `Cancelled` only before pickup. Where it is lives on the parcel: `CurrentHubId`,
`TransferToHubId` or `RiderId`, at most one (SQL check). Every move writes a `ParcelEvent` (status, note, hub).
On hold is allowed while attempts remain (`Tenant.MaxDeliveryAttempts`, 3); the last failed attempt returns it.

### Money
Each finished parcel writes its ledger lines in the same save (`LedgerEntry.For`): `Cod` (+ collected),
`DeliveryCharge` (−), `CodCharge` (−), `ReturnCharge` (−); ৳0 lines are skipped. The hourly `merchant-payouts` job pays
each merchant every unpaid line up to yesterday (courier's time zone) as a `Payout` (`INV-000001`) through the payout
gateway (fake in Development, listed on `/Dev/Payouts`) with the key `{slug}-payout-{id}`; a merchant whose lines
come to ৳0 or less, with no payout account, or whose payouts the courier holds (`Merchant.PayoutHold`), waits. A
transfer the gateway refuses stays `Pending` with what it said (`FailedAttempts`, `LastError`) and is sent again every
hour; the admin can send it again or cancel it (`Cancelled`, its lines freed). The courier settles or corrects a
balance with an `Adjustment` line (no parcel, a reason the merchant reads). A merchant whose charges come to more
than its cash can pay what it owes online through SSLCommerz (`OnlinePayment`, `PAY-100001`): once the gateway, asked by
our server, confirms the money, the payment writes one `Adjustment` credit, and never a second. The rider's cash is
checked when hub staff close the run (`CashExpected` vs `CashReceived`); payouts do not wait for it.

### Multi-tenancy
A **tenant is a courier operator** with its own hubs, zones, areas, rates, riders and merchants. One courier is seeded
(OneDrop Courier, slug `onedrop`); the integration tests add a second ("rival", in `OneDrop-Test` only) to prove the
isolation. Inside a courier each merchant sees only its own parcels and each rider only the parcels given to them.

---

## 2. Where we are

The traditional courier rebuild and Phases 3 and 4 so far are on `main` (pushed as `ca16f42` on 2026-10-08; the work
since is uncommitted for the owner's review). The app is ready to go live for the owner's portfolio: `deploy/` runs it
on one Linux server with a clean database and one admin, and [Deployment.md](Deployment.md) is the guide for the
owner's senior, who chooses the server and the address. The plan's "Today" says what is next.

---

## 3. What exists today

### Solution layout (`Courier.sln`)
| Project | Path | Contents |
|---|---|---|
| Domain | `src/Domain` | `Common` (Entity with domain events, TenantEntity, Result/Error, PhoneNumber), `Platform/Tenant`, `Network` (Hub, Zone with `City` and `IsSuburb`, Area), `Pricing` (ServiceArea, DeliveryRate, ParcelCharges), `Merchants` (Merchant with status, payout account, webhook; MerchantApiKey; PickupPoint; Moderator with MerchantPermissions; WebhookSignature), `Staff` (StaffMember, StaffJob, StaffPermissions), `Parcels` (Parcel and its state machine, ParcelEvent, ParcelStatusChanged), `Delivery` (Rider, DeliveryRun, DeliveryAttempt, PickupRequest), `Payments` (LedgerEntry, Payout, OnlinePayment), `Notifications` (OutboxMessage) |
| Application | `src/Application` | Use cases as vertical slices: `Parcels` (CreateParcel, Browse, ParcelActions, Labels, Track, Quote, FraudCheck, BulkImport), `Hubs` (HubDirectory, HubScan, HubBoard, AssignParcels, Runs), `Delivery` (Pickups, RiderDay, Riders), `Merchants` (Onboarding, Admin, Account, ApiKeys, Webhook, Moderators with MerchantAccess), `Payments` (RunPayouts/PayoutsJob, MerchantPayments, AdminPayouts, OnlinePayments + CheckOnlinePaymentsJob), `Pricing/Rates`, `Network` (ListAreas, Coverage), `Dashboards`, `Notifications` (outbox contracts, RecipientTexts + SendOutboxJob, SendWebhooksJob, FailedMessages). Interfaces: `IAppDbContext`, `ITenantContext`, `ITenantCatalog`, `ICurrentUser`, `ISmsSender`, `IWebhookSender`, `IPayoutGateway`, `IPaymentGateway`, `ITrackingLinks`, `IUserAccounts`, `IOperationsFeed`, `ITenantJob` |
| Infrastructure | `src/Infrastructure` | `Persistence/AppDbContext` (query filters, outbox writer, `AcrossTenantsAsync`), `Configurations/*`, `TenantSaveInterceptor`, `TenantSessionInterceptor`, `MultiTenancy`, `Identity` (AppUser, UserAccounts), `Sms/FakeSmsSender`, `Payments/FakePayoutGateway`, `Payments/SslcommerzGateway` and `FakePaymentGateway`, `Webhooks/HttpWebhookSender`, `Seeding` (DemoDataSeeder, DemoActivity, AdminSetup for a live site's first admin), `Jobs` (Hangfire, TenantJobRunner, TenantJobRegistry, OutboxDispatcher) |
| Web | `src/Web` | Razor Pages: public site (`/`, `/Track`, `/Coverage`, `/Account/*`), `Merchant/*`, `Hub/*`, `Rider/*`, `Admin/*`, `Platform/Tenants`, `Dev/*`; `Api/V1` (parcels, charge, areas); `Live` (SignalR); `Display` (`Icons`, `Statuses`, `Money`); `wwwroot/css/site.css` (the design system, with its motion), `wwwroot/css/landing.css` (the front page's drawings) and `wwwroot/js/site.js` (shared behaviours: count-up, busy buttons, confirm dialog, toasts, tips, "/" search, drawings resting off screen, sections rising in); `Pages/Shared/Art/*` (the front page's line drawings), `Pages/Shared/_BrandMark.cshtml` (the logo); `wwwroot/images/logo.svg` and `logo-on-dark.svg` (the logo with its name, outlined), `wwwroot/icons` (app icons) |
| Database | `src/Database` | SQL project (Microsoft.Build.Sql 2.1.0) → `Database.dacpac`. Owns the schema |
| Database Update | `src/Database Update` | DbUp: `Scripts/2026/001_SeedCourier.sql` (the courier, 16 hubs, 21 zones, 87 areas, the rate card); `Scripts/Pre` empty |
| Tests | `tests/Domain.Tests`, `tests/Architecture.Tests`, `tests/Integration.Tests` | 242 domain + 6 architecture tests, all passing, and the integration suite against `OneDrop-Test` (the plan's daily log says what each task ran) |
| Tools | `tools/db/publish.ps1`, `tools/Simulator` | Deploy a database; book made-up shops' parcels through the running app's API (`--parcels 40 --pace 2`) |
| Docker and CI | `Dockerfile`, `docker-compose.yml`, `.github/workflows/ci.yml` | Compose = SQL Server 2025 + deploy + app on `onedrop.localhost:5080` (Development, with the demo logins); CI deploys a throwaway SQL Server from nothing and runs every suite |
| Live site | `deploy/` (`docker-compose.yml`, `Caddyfile`, `.env.example`), `Documentation/Deployment.md` | SQL Server Express + deploy + the app as Production + Caddy (HTTPS) on one Linux server; the courier on one address (`Tenancy:HomeCourierHost`), its first admin from `Setup:AdminEmail` / `Setup:AdminPassword`, or the demo admin (`Setup:DefaultAdminEmail` / `DefaultAdminPassword`) when none is named |

### Features
- **Booking.** `CreateParcelHandler` serves the API, the form (`/Merchant/NewParcel`, a live quote beside it) and the
  CSV upload (`/Merchant/BulkUpload`: every row booked in one transaction or none, with the rows to fix; a template to
  download). Area by id or name; the pickup point defaults to the merchant's default; the phone is normalised to E.164;
  charges are snapshot from the rate card. Idempotency per merchant and request hash (same key + same body → 200 with
  the same parcel, other body → 409). A merchant not `Active` cannot book (403 `parcel.merchantNotActive`).
- **Every panel.** The menu is one list (`Display/Menu.cs`) that draws the sidebar and the favourites: a bar under the
  top bar (merchant, admin, hub) of up to eight pages a person pins with the menu's stars or the "Add" picker, kept in a
  cookie per login (`favourites-{userId}`) with defaults per role. The avatar at the top right opens the account menu
  (name, email, role, account pages, change password at `/Account/Password`, sign out).
- **Delivery types and the door on a map** (2026-10-12). The booking form, the CSV upload (`speed`, `product_type`,
  `location`) and the API (`speed`, `productType`, `recipientLatitude`, `recipientLongitude`) take a speed and what is
  inside, priced live with each option's extra or "not offered here". The API and CSV name an option by its code
  (`express`, `fresh-food`; made from its first name and never changed) or its name, in any spelling ("Fresh food",
  `freshFood`); left out, the courier's default. The recipient's door can be pinned: on Google's
  map (search, click, drag, "find the address") when `GoogleMaps:ApiKey` is set, or by pasting the Google Maps link
  or coordinates the customer sent (the server reads every link form and opens `maps.app.goo.gl` short links,
  `Infrastructure/Maps/GoogleMapLinks`, Google's short hosts only). Riders tap Navigate for Google Maps directions;
  parcel pages show the pin (a small embedded map with a key). Merchant menu: "Manage businesses" (adding one starts
  there), "Pickups" (read-only: the rule, today's and tomorrow's parcels per point, the latest pickups).
- **The pickup point on the map, and the rider's route** (2026-10-16). A merchant pins its own door too, not just the
  recipient's: `/Merchant/PickupPoint/{id}` (from Settings, where each point says "Pinned" or "By address") is the
  pickup point's own page with the same pin box as the booking form (`Merchants.PickupPoint.Latitude`, `Longitude`;
  the box now names the form's area and address fields, `data-pin-area` and `data-pin-address`, so one script serves
  both). The rider's app shows Navigate and Call per pickup stop, and a **route card** on the home page and on
  Pickups: one Google Maps link through every stop of the round in the order it is worked, pickups first and then the
  doors, starting where the rider is now (`MapLinks.Route`; a stop is its pin, or its address for Google to look up,
  so a round with nothing pinned still has a route). Google takes a destination and nine waypoints, so a longer round
  is cut to the first ten stops (`MapLinks.MaxRouteStops`) and the card says to open it again from the next stop.
- **Delivery types the admin keeps** (2026-10-13). `/Admin/DeliveryTypes` (setup permission; menu Setup, after the
  rates) opens with a worked example of how a delivery charge adds up (rate card + speed + contents, from the courier's
  own numbers), then a table per kind: each option with its badges and what it adds in each service area ("+৳40, next
  day", "No extra", "Not offered"), up and down arrows and Edit. Edit and Add open a window of three steps: how
  merchants see it (name, short description, icon, with a live preview of the booking form's card), where it is
  offered and what it adds (a tick per service area, its extra, and for a speed its own days, empty for the rate
  card's), and at the hub (the contents' handling note, "goes first"). Make another the default (the new one
  goes everywhere free, the old one is offered nowhere until priced) and stop offering (archived: parcels keep it;
  "offer again" brings it back with its prices). Up to 12 of each on sale; names unique per kind among those on sale.
  The booking form, the merchant's rate card and calculator, the front page's price cards, the API's charge endpoint
  (`speeds`, `contents`) and the CSV upload all read them. Parcels keep the extra they were booked at and show the
  option's current name.
- **Merchant panel.** Dashboard ("Right now" by stage with cash, quick actions, tools, last 30 days, last 7 days, money
  and last payment), parcels with tabs (partly delivered has its own; in review and exceptional show while a hub has
  flagged one), search, date range and
  CSV export, parcel page (progress, route, money, attempts, history; edit and cancel before pickup, ask the courier
  to cancel it or change its cash once it is on its way), labels (QR, A6, hub code), pickup requests, returns,
  requests, payments (statement and payouts) and invoices, fraud
  check, rate card and calculator, settings (profile, payout account bKash / Nagad / bank, pickup points), API keys,
  webhook.
- **Returns the merchant signs for.** The hub that collected returning parcels sends them back with one of its riders
  on a **return list** (`RL-100001`) per pickup point (`/Hub/Returns`, also the hub board's "Hand back" step), or
  hands them over at its counter as before. The rider's app lists the lists to hand back; at the merchant's door the
  rider records "handed over" (each parcel is returned, and its delivery and return charges go to the ledger in the
  same save) or why not (the parcels stay with the rider until the hub scans them in, then can go on a new list). The
  merchant follows its returns on `/Merchant/Returns` (what is coming back and where each is, and the lists) and
  confirms a handed-over list on `/Merchant/Return/{number}`, its signature for the parcels, with a note when
  something is missing; the dashboard asks until it does, and the hub sees the note. A flagged parcel does not go out
  on a list.
- **Requests to cancel or change the cash.** Once a parcel is on its way (picked up, at a hub, between hubs, out for
  delivery or on hold) the merchant no longer changes it itself: from the parcel's page it asks the courier to
  **cancel** it (it comes back as a return, charged delivery plus return) or to **change its cash on delivery**, with
  a reason. One open request per parcel. The courier's admins answer on `/Admin/Requests` (also a banner on their
  dashboard): approving changes the parcel there and then (a history line says so; the COD charge follows the new
  amount), refusing needs a reason the merchant reads on the parcel and on `/Merchant/Requests`. A cancellation
  cannot be approved while a rider has the parcel; it stays open until the rider brings it back. This replaces the
  merchant's direct "Ask for it back"; hub staff still return a parcel directly from its hub page.
- **Work marks in the menu.** A red "!" on a menu item (and its favourite, and the phone's menu button) while open
  work waits there (`MenuWorkHandler`): for the admin sign-ups, requests, flagged parcels, hub work, failed messages
  and stuck payouts; for a merchant parcels with no pickup asked for, return lists to confirm, no payout account; for
  a rider deliveries or return lists, and pickups. A filter whose part is marked `data-swap` (the payouts card) swaps
  only that part when clicked instead of reloading the page (`site.js`).
- **The admin's bell.** A bell in the admin's top bar lists what waits for them (`AdminBellHandler`), each as one
  line with its count and a link: sign-ups to approve, requests to answer, parcels flagged in review or exceptional,
  messages that failed, payouts the gateway refused, merchants owed money with no payout account; and for the last
  week, riders who handed in less cash than they collected and notes merchants left on return lists. Worked out when
  the page is drawn, like the merchant's; a line shows as new when something joined it since the bell was opened.
- **Payouts the admin steers.** `/Admin/Payouts` explains how payouts work, says when the hourly run last ran and runs
  next (and warns when it has not run for two hours), lists the transfers the gateway refused with its reason (send
  again, or cancel on the invoice so the lines go to a corrected account), what each merchant is owed and, apart, the
  merchants who owe the courier. On `/Admin/Merchant/{id}` the admin holds the account's payouts with a reason (the
  merchant reads it on Payments and cannot ask to be paid now), releases them, pays the business now, and writes an
  adjustment (a credit when the merchant paid the courier or is compensated, a charge otherwise), which shows on the
  merchant's statement and invoice with its reason. `/Dev/Payouts` can make the fake gateway refuse an account.
- **Paying what is owed, online (SSLCommerz).** A merchant whose charges come to more than its cash reads "You owe
  the courier ৳X" on `/Merchant/Payments` and can pay it online ("Pay ৳X online"), or let it come off the next cash
  collected. The amount is worked out on the server from the merchant's own lines; the payer goes to SSLCommerz's page
  (in Development without a store, the test page `/Dev/Pay/{transaction}`) and is posted back to
  `/pay/{transaction}/success|fail|cancel` on the courier's host, which is open to anyone because nothing posted is
  believed: our server asks the gateway (validation by `val_id`, or the transaction query) before anything is
  recorded, and lands the payer on Payments with a banner for that payment. The gateway's own notice
  (`/pay/notice`) is handled the same way, and the `online-payments` job (every 10 minutes) asks about every payment
  still waiting and every one that failed in the last day, so money taken from a payer whose browser never came back
  is still credited; one still waiting after a day is closed as not finished. Confirmed money writes exactly one
  `Adjustment` credit ("Paid online by bKash, PAY-100001", shown as "Paid online" on statements and invoices), even
  after the page said it failed. A payment the gateway marks risky, or confirms for another amount, is held
  (`Review`): the admin sees it on `/Admin/Payouts` and in the bell, and credits it or records that it was refunded
  through SSLCommerz's panel. The merchant's Payments page lists its online payments; the admin's lists the latest.
- **Recipients pay the COD online** (4.10, 2026-10-12). The tracking page offers "Pay online" while a parcel with
  cash to collect is on its way (booked up to out for delivery or on hold) and the gateway is set up; it never names
  the amount. `/Track/Pay/{code}` shows the amount only after the recipient gives the phone number the parcel is for,
  asks for an email for the gateway's receipt and starts an `OnlinePayment` with the parcel's `ParcelId` for the
  parcel's own COD. It is settled exactly like a merchant's payment (return, notice, check job; nothing posted is
  believed), but writes no ledger line: the parcel takes it (`Parcel.TakeOnlinePayment` → `PaidOnline`), the rider's
  app and run sheet show ৳0 "already paid", the rider records the delivery with ৳0 (a paid parcel cannot be partly
  delivered), the attempt keeps the rider's ৳0 cash and the parcel's `CollectedAmount` is the whole COD, so the
  merchant's Cod line, COD charge, statements and reports are unchanged. Once paid, the COD cannot change (edit,
  request, approval all refuse). Money the parcel cannot take (paid at the door first, a second payment, another
  amount, cancelled meanwhile) is owed back at once (`RefundDue`); a paid parcel that is cancelled or returned is owed
  back by the check job; the job then asks SSLCommerz's refund API (`refe_id` = the payment's number), records the
  gateway's refund reference, and asks again hourly if it refused. The admin sees refunds owed on `/Admin/Payouts`
  (and a bell line and menu mark for refused ones) and can record a refund made by hand. The merchant sees "paid
  online by the recipient" on the parcel; its own Payments page lists only its own payments.
- **Staff jobs and permissions** (5.1, 2026-10-12; 4.4 before it). A staff login (role `HubStaff`) has an
  `Identity.StaffMember` row: one hub, or head office (no hub, every hub), a job (`Domain.Staff.StaffJob`) and its
  permissions (`StaffPermissions`, a bit field): the job's preset plus whatever was ticked, because people cover each
  other's desks. Head office work (operations, merchants, finance, setup) is never given at a hub. Super admins
  (`TenantAdmin`) have no row and may do everything. `StaffPermissionFilter` holds every `/Hub` and `/Admin` page,
  and buttons that change something ("page/handler"), against the default-deny list in
  `Web/Authentication/StaffPages.cs`, like the moderators' list; menus, favourites, the bell, work marks and the
  dashboard's buttons only offer what the person may open, and each person lands on their own desk. A hub's own
  staff are locked to their hub: `HubOverviewHandler` lists only it (no hub menu, another hub's code is not found)
  and the parcel page's buttons refuse a parcel at another hub. The scanner offers only the modes of their desks.
  `/Admin/Staff` (staff permission): add with a first password shown once, change job, hub and ticks, stop, let back
  in, new password. A super admin manages everyone and alone makes super admins; head office with the staff
  permission everyone below; a hub manager only their own hub's people, at their hub's desks. Nobody gives a
  permission they do not hold or changes someone who may do more; nobody stops or demotes themselves, and the last
  super admin stays one. Permissions are read on every request, so a change applies from the next click; a stop or a
  move in or out of super admin reaches an open session within a minute. A hub with staff cannot close. Not yet:
  sorting (5.3, no racks yet), a desk home per job and "who did it" (5.2).
- **Payout accounts and getting paid now.** A merchant account keeps several bKash, Nagad and bank accounts
  (`Merchants.MerchantPayoutAccount`, on the account's main profile like moderators) and chooses the one payouts go
  to, which is copied onto the main profile as before so every business, the payout run and the admin's page read it
  unchanged; one in use cannot be removed, and an account the admin sets is kept among them. `/Merchant/Payments`
  splits the balance into what the next payout run sends by itself (up to yesterday) and what came in today, shows the
  cash still to collect on parcels on their way, and offers **Ask to be paid now** (2026-10-08; it replaced a "Get
  paid now" that paid at once). The merchant asks (`Payments.PayoutRequest`, one open per business, which it may take
  back); the courier's admin sees each request on `/Admin/Payouts` and in the bell with what is at stake: the balance
  now and how much of it is today's, the cash still with riders whose run is not closed, and the delivery and return
  charges the merchant would owe if its parcels on the way came back. The admin pays (every line not yet paid out,
  today's too, in one payout sent at once through `PayoutsJob.PayNowAsync`, the run's own rules) or refuses with a
  reason the merchant reads on Payments and in its bell; the daily payouts carry on either way.
- **The bell.** A bell in the merchant's top bar lists what happened over the last week, newest first: deliveries
  (a line a day with the cash), partly delivered, held or refused at the door, back with the merchant, problems a hub
  flagged, return lists to confirm, answered requests and payouts sent; a count of what came since it was last
  opened. Nothing new is stored: the notices are worked out from the parcels, attempts, lists, requests and payouts,
  and "seen" is a cookie per login (`bell-{userId}`) like the favourites. A moderator is told only what their
  permissions show.
- **Small things.** `/Merchant/Export` downloads every parcel of a stage, days and search as CSV (up to 10,000, with
  the charges); the parcel list's Export button uses it with its own filters. `/Merchant/ByDate` lists each day of a
  period with the parcels booked and where they stand, each linking to that day's parcels. The merchant's ID shows
  with a copy button in the account menu and on Settings, and the admin's merchant search finds it. Empty lists say
  what to do next (book a parcel, clear the filters).
- **Stats.** `/Merchant/Stats` sums up the parcels booked in the last 7 or 30 days, this month, last month or the
  merchant's own days (at most a year): booked with their cash, delivery, return and cancel rates, cash collected,
  where they are now as one bar, and count, cash and share per status, each linking to those parcels. Moderators see
  it with the dashboard permission.
- **Problems a hub flags.** Hub staff or an admin flag a parcel the courier has as **in review** (something to
  check: the address, the contents, the packing) or **exceptional** (something went wrong: damaged, opened, missing),
  with what is wrong, on `/Hub/Parcel/{code}`, and clear it with what was done. The flag sits beside the status, which
  does not change: the parcel can still be scanned, sent between hubs and returned, but it is not handed to a rider
  (the assign page offers no box for it and `Parcel.AssignTo` refuses). The merchant sees it in the tab, on the
  parcel's page and in a banner on its dashboard, and the API returns it (`issue`, `issueNote`); the admin's dashboard
  counts both kinds. Flagging and clearing are written to the parcel's history but post no webhook (the status did
  not move).
- **Several businesses per account.** A merchant adds up to ten businesses (the courier's setting) (`/Merchant/NewBusiness`: name, phone, address, first pickup
  point) and switches between them from the sidebar or the account menu, or from `/Merchant/Businesses` (each with its
  parcels on the way and money waiting). One login works in all of them; each keeps its own parcels, pickups, payments,
  balance, API keys and webhook. Approval and the payout account are shared from the account; the admin sees which
  account a business belongs to.
- **Moderators.** A merchant account's owner adds people who work in the account with a sign-in of their own
  (`/Merchant/Moderators`: name, email, phone, and tick boxes for dashboard, parcels, booking, payments, tools and
  settings). Each is a `Merchants.Moderator` row pointing at the account's main profile and an ordinary merchant
  login, so a moderator works in every business of the account and sees only that account's parcels and money. We
  send no email yet, so the page shows a first password once, to hand over (and can issue a new one); the person
  changes it at `/Account/Password`. Stopping one locks the login out at once and keeps the row. Pages are held by
  `MerchantPermissionFilter` against the list in `Web/Authentication/MerchantPages.cs` — a merchant page missing
  from that list is the owner's alone — and the menu, favourites and dashboard only offer what the person may open.
  Moderators themselves are the owner's: a moderator never sees that page.
- **Sign-up and approval.** `/Account/Register` makes a `Pending` merchant with its login and default pickup point and
  signs it in; the admin approves (`Active`), suspends or reactivates on `/Admin/Merchant/{id}`, or adds a merchant
  directly (`/Admin/NewMerchant`, active at once).
- **Vehicle limits that hold** (2026-10-13). `/Admin/Vehicles` keeps what each vehicle carries at once (parcels, load,
  heaviest parcel); every hand-over now checks it against what the rider already has. `/Hub/Pickups` shows a card per
  pickup van with its load as a meter, offers only vans, says how many trips the round takes, and warns the hub that
  has no van; `/Hub/Assign` greys out a parcel heavier than any vehicle of the hub takes and says so before the
  hand-over; `/Admin/Riders` names the staffed hubs with no van and the dashboard's setup checklist asks for one. A
  rider's vehicle cannot change while they carry anything (their load was sized for the old one).
- **Hub.** "Choose your hub" first (`/Hub/Choose`: every hub with its waiting work, busiest first; the choice is
  remembered until the next sign-in, and the hub menu on each page shows each hub's waiting count). The courier's
  admins start instead on **all hubs** (2026-10-12): the board summed over every hub, each list saying which hub its
  work is at and each step opening the chooser for its page; the board's hub menu offers "All hubs" to anyone
  (`?hub=*`, remembered like a hub), while scan, assign, closing, pickups and returns still work at one hub. Board (live counts: pickups open, coming from other hubs, waiting for a rider, to send on, out with riders,
  to hand back, cash with riders), scan page with three modes (Receive, Dispatch, Hand back; the answer says what to do
  next), weigh a parcel on the parcel page (the charge follows the scale), pickups (assign a rider of this hub), assign parcels to a rider (opens the rider's `DeliveryRun` for the day and
  a `DeliveryAttempt` per parcel), rider closing (cash received against expected; held and refused parcels go back on
  the shelf), parcel search and page (ask for a return), label reprints.
- **Rider app.** Today: cash in hand, stops with address, phone, COD; a delivery page per parcel with Delivered,
  Partly delivered (amount and reason), On hold (reason and date), Refused (reason); pickups to collect (a pickup van
  only, and only what the van still holds).
- **Admin.** Live dashboard, read top to bottom: a setup checklist while the courier is not set up, "Waiting for you"
  (the admin bell's lines as a to-do list), the parcels' day across every hub as six numbered steps that open the hub
  with that work, then today's counts, stages, last 7 days, hubs and top merchants; a guide (`/Admin/Guide`, "How it
  works": the setup order, a parcel's day with the page for each step, what needs the admin); a menu ordered by when
  it is used (every day, money, people, setup); toasts that offer the next step, merchants, riders (add with a login, edit, stop), payouts (owed per merchant, run now, payouts and
  invoices), rates (change for new bookings only), coverage (the map, and tabs that open and close hubs, draw zones on
  them and keep the areas), failed messages (send again).
- **Public.** Landing page: the promise, a tracking box and the courier's real counts (cities, hubs, areas) beside an
  animated line drawing (a rider driving round a little planet of shops, a hub and homes, parcels on parachutes, cards
  following one parcel), a ribbon of services, the four steps drawn and animated, services, the rate card and a closing
  call to action; tracking page (status per step, the hub, a hold's reason; never the address, phone or amounts; rate
  limited per address); coverage list.
- **Texts and webhooks.** `Parcel` raises `ParcelBooked` once, at booking, and `ParcelStatusChanged` on every move after
  that; the save writes a `RecipientTextMessage` for the booking (a tracking link), for out for delivery, delivered and
  partly delivered, and a `ParcelStatusChangedMessage` (webhook) for every move — booking itself never posts to the
  webhook, since the merchant made that change itself. Texts are written at send time and skipped when out of date;
  webhooks follow Standard Webhooks (`parcel.status_changed`). Retries after 1, 2, 4, 8 minutes, then given up.
- **Fraud check.** Counts a phone's parcels at every merchant of the courier (delivered, partly, returned, in
  progress) with the merchant filter lifted, and returns counts and a verdict only.
- **Live dashboards.** After a committed save touching parcels, runs, attempts, pickups or riders, the courier's SignalR
  group hears "changed" and the page re-reads its `data-live` parts.
- **Coverage, kept by the admin.** `/Admin/Coverage` opens as the map it always was and carries three more tabs: hubs
  (open one, edit it, close it), zones (code, name, city, suburb flag and the hub that serves it) and areas (name and
  zone, filtered by zone). The seed still lays the map out; nothing on it is ever deleted, only archived, so parcels
  keep the route and the charge they were given. Guards: a hub closes only once its zones, riders, parcels and pickups
  have gone; a zone leaves the map only once its areas have, and keeps its hub, city and suburb flag while parcels
  priced by them are still moving; an area stays put while parcels are on their way there or a merchant collects from
  it; a zone or area comes back only onto an open parent. Codes are letters and digits, unique per courier, and area
  names are unique per courier.
- **Email to merchants.** A real mail server (`Email:*` in the git-ignored `appsettings.Local.json`; Gmail over SMTP
  with an app password today) sends a merchant its payout email the moment the money leaves: what was collected, the
  charges, what was sent, to which account, and a link to the invoice. It goes through the outbox like every other
  message, in a loop of its own (`SendEmailsJob`), so a slow mail server never holds up a text or a webhook; with no
  mail server configured the fake keeps them for `/Dev/Emails`, which also has a button that sends a test email. The
  integration tests always use the fake.
- **Courier settings** (2026-10-12). `/Admin/Settings`: the courier's name, hotline, SMS sender (letters and digits,
  at most 11), delivery attempts, businesses per account, the pickup time (when the van leaves the hub), and when riders are due
  back, and after how many days a parcel is stuck. Saving clears the cached courier (`ITenantCatalog.Forget`), so
  every page and job reads it from the next request. The address name, time zone and currency are shown but stay the
  platform's.
- **Stuck parcels** (2026-10-12). `/Admin/Stuck` lists, the longest first, parcels that have not moved for the
  courier's `StuckAfterDays` (3 for OneDrop Courier): with the rider who picked them up, at a hub, between hubs, going
  back, or on hold that many days past the day they were held for (out for delivery is not watched: runs close daily).
  A parcel moves when its status or place changes (`Parcel.MovedOn`, stamped by the save interceptor); a note such as
  a flag or a weighing does not count. The rule is one query (`StuckParcelsHandler.Stuck`) shared by the page, a line
  in the admin's bell and a mark in the menu.
- **A page per rider** (2026-10-12). `/Admin/Rider/{id}` (the name on `/Admin/Riders` opens it): what the rider carries
  now and the cash in hand, then over the last 7 or 30 days, a month or chosen days, their doors, deliveries and
  success rate, cash collected, handed in and short at closing, each run (with its run sheet and closing while open)
  and their latest 30 hand-overs with reasons.
- **The whole merchant for the admin** (2026-10-12). `/Admin/Merchant/{id}` adds, from any business of an account:
  parcels on the way and the return rate, the account's businesses (each with its parcels on the way and balance),
  its moderators and what they may use, the merchant's saved payout accounts, the business's latest payouts and its
  requests (about parcels and to be paid now).
- **Reports.** `/Admin/Reports` over any period up to 92 days (a week by default): day by day (delivered, returned,
  cash collected, each kind of charge, what the courier earned and what it owes), by rider (stops, deliveries, cash
  collected against cash handed in, what is short) and returns by merchant (rate and return charges). Each table
  downloads as CSV.
- **Run sheet.** `/Hub/RunSheet/{run}`: the paper a rider carries, with every parcel still to hand over, the door,
  the phone, the cash to collect and a line to sign; offered as soon as parcels are assigned and from the closing
  page while the rider is out. A parcel the rider has recorded leaves the sheet.
- **Not yet:** real SMS and bKash/Nagad/bank payout gateways (fakes; the owner has no accounts for them yet; SSLCommerz
  takes money in and cannot send it to merchants), the app's own SQL login instead of `sa`.

### Database
- 2026-10-16 (emptying the live site): no schema change. `Scripts/2026/014_EmptyTheLiveSite.sql` empties one database,
  the live site's (`IF DB_NAME() = N'Delyvo'`, so the development `OneDrop`, the test `OneDrop-Test` and a database
  deployed from nothing are left alone), of merchants, businesses, moderators, API keys, pickup points, parcels and
  their history, riders, runs, attempts, pickups, return lists, the ledger, payouts, online payments, the outbox and
  every login but the courier's super admins, and starts the tracking, invoice, payment and return list numbers again.
  It keeps the courier and its settings, the hubs, zones and areas, the rate card, the delivery types and the
  vehicles, and does nothing if the courier would be left with no super admin. `sql/EmptyTheCourier.sql` is the same
  by hand, for any courier in any database, and is never deployed.
- 2026-10-16 (demo admin, lockouts): no schema change. `Scripts/2026/012_DemoAdminPassword.sql` sets the demo password's
  hash (Identity v3), a new security stamp and no lockout on `admin@onedrop.test` where it is a TenantAdmin of
  `onedrop`, and does nothing where there is no such login (it does not check `Archived`, so a demo admin someone had
  stopped would be let back in; nobody could have stopped it on the live site, where nobody could sign in).
  `013_ClearOldLockouts.sql` then clears `LockoutEnd` and `AccessFailedCount` on every login that was not stopped
  (`Archived = 0`), because the old five-try rule's locks outlive the rule and sign-in refuses a lock whatever the
  setting.
- 2026-10-16 (places): `Network.Zone.Division` (NULL: not filed; one of the eight divisions). `Scripts/2026/010_BangladeshPlaces.sql` files the launch zones under their divisions, adds the other 53 districts as zones (city = the district, so another district is outside city; served by the nearest hub) and every upazila as an area (74 zones and 572 areas in all; a name used twice gets its district in brackets). The address picker (`_AreaSelect`, `site.js`) is division, then zone, then area; the admin sets a zone's division on `/Admin/Coverage`.
- 2026-10-13 (vehicles): no schema change. `Scripts/2026/009_PickupsOnlyForPickupVans.sql` puts back on its hub's list
  every pickup that had been given to a rider on anything but a pickup van and not yet collected.
- 2026-10-13 (pickups): `Platform.Tenant.PickupCutoff` is gone: `Scripts/Pre/005_DropPickupCutoff.sql` drops it from a
  database that has it, and `Scripts/2026/005` no longer sets it (a dated line in its header says why). No other
  change: `UX_PickupRequest_Point_Date_Open` stays on open pickups, because older databases hold several completed
  pickups for one point and day, so the code, not an index, keeps the van from a point twice a day.
- 2026-10-13: `Pricing.DeliveryOption` (a speed or kind of contents: kind, code, name, description, handling, icon,
  goes first, default, order; one default per kind, `UX_DeliveryOption_Tenant_Kind_Default`; archived, never deleted)
  and `Pricing.DeliveryOptionCharge` (its extra per service area and a speed's days; no row: not offered), both in the
  policy. `Parcels.Parcel.Speed` and `ProductType` (TINYINT) became `SpeedId` and `ProductTypeId` (keys to the option)
  and `Pricing.DeliveryRate` lost its six extras columns: `Scripts/Pre/004_DeliveryOptions.sql` made the tables, gave
  every courier the seven options it had, copied the prices and pointed every parcel at its options; a new database
  gets them from `Scripts/2026/008_DeliveryOptions.sql`. `005` no longer sets the dropped columns (a dated line in its
  header says why), since a new database could not run it otherwise.
- 2026-10-12 (4.10): `Parcels.Parcel.PaidOnline` (`chk_Parcel_PaidOnline`: positive and at most the COD) and on
  `Payments.OnlinePayment` `ParcelId` (FK to the parcel; NULL for a merchant's own payment), `RefundReference`,
  `RefundTriedOn`, `RefundedOn` and status 6 RefundDue; `chk_OnlinePayment_Credit` lets a parcel's payment be paid with
  no ledger line, and `UX_OnlinePayment_Parcel_Paid` keeps one paid payment per parcel. Nullable columns only, so no
  `Scripts/Pre`; both databases published.
- 2026-10-12 (later): `Platform.Tenant.StuckAfterDays` (NULL: not watched) and `Parcels.Parcel.MovedOn` (when its
  status or place last changed; `IX_Parcel_Tenant_Status` includes it). `Scripts/2026/006_StuckParcels.sql` sets 3 days
  for OneDrop Courier and gives older parcels the time of their latest history line.
- 2026-10-12: `Parcels.Parcel.Speed`, `ProductType`, `SpeedCharge`, `ProductCharge` (both extras part of
  `DeliveryCharge`), `RecipientLatitude`, `RecipientLongitude`; `Pricing.DeliveryRate.ExpressCharge`, `ExpressDays`,
  `LiquidCharge`, `FragileCharge`, `FreshFoodCharge`, `FrozenCharge` (NULL: not offered); `Platform.Tenant.PickupCutoff`,
  `PickupTime`; `Delivery.PickupRequest` lost `ExpectedParcels` and `Note`, status 1 is now Scheduled, and
  `UX_PickupRequest_Point_Date_Open` keeps one open pickup per point and day. `Scripts/Pre/003_DeliveryTypesAndPickups.sql`
  backfills old parcels (regular, general, no extras) and drops the two columns; `Scripts/2026/005` seeds the extras
  and the 3:00 pm cut-off and 3:30 pm pickup.
- `Merchants.Merchant.MainMerchantId` (2026-10-06): set on a business added to another merchant's account, pointing at its main profile (`FK_Merchant_Merchant`, never itself).
- `Merchants.MerchantPicture` (2026-10-06): the picture (PNG, JPEG or WebP, at most 512 KB, judged by its first bytes) of a main profile or a business, one row per merchant, in a table of its own so that reading a merchant never loads the bytes. Uploaded in `/Merchant/Settings` for the business being worked in and served by `/Merchant/Picture/{id}` to logins of the same account only; it shows in the sidebar, the account menu, the business switcher and `/Merchant/Businesses`.
- `Merchants.Moderator` (2026-10-06): a person who works in a merchant account with a login of their own, and what
  they may do (`Permissions`, a bit field; see `Domain.Merchants.MerchantPermissions`). It carries `AccountId` (the
  account's main profile) instead of the usual `MerchantId`, because a moderator works in every business of the
  account and so is not merchant-filtered; `UserId` is unique per courier. Stopping one archives the row.
- `Merchants.MerchantPayoutAccount` (2026-10-07): the payout accounts a merchant account keeps (method, number,
  name), by `AccountId` like `Merchants.Moderator`, archived when removed, a number kept once per account. The one in
  use stays on the main profile's `PayoutMethod`, `PayoutAccount` and `PayoutAccountName`;
  `Scripts/2026/004_PayoutAccounts.sql` copied each account's existing one in.
- `Parcels.ParcelRequest` (2026-10-07): a merchant's request about a parcel on its way (kind 1 cancel, 2 change the
  cash on delivery with `NewCodAmount`), its reason, who asked, and the courier's answer (status 1 open, 2 approved,
  3 refused). One open request per parcel (`UX_ParcelRequest_Parcel_Open`); merchant-filtered and in the policy.
- `Delivery.ReturnList` and `Delivery.ReturnListParcel` (2026-10-07): returning parcels a hub sends back to one
  pickup point of a merchant with a rider (status 1 out, 2 handed over, 3 confirmed by the merchant, 4 not handed
  over), with its parcels; numbered from the sequence `Delivery.ReturnListNumber` (`RL-100001`). Both carry
  `MerchantId` and are in the security policy.
- `Parcels.Parcel.Issue`, `IssueNote`, `IssueRaisedOn` (2026-10-07): a problem a hub flagged (1 in review, 2
  exceptional), what it is and when it was first flagged; all three NULL while there is none (`chk_Parcel_Issue`),
  with the filtered index `IX_Parcel_Tenant_Issue` for the tabs.
- `Payments.Payout` `FailedAttempts`, `LastError`, `LastTriedOn` and status 3 Cancelled; `Payments.LedgerEntry` kind 5
  Adjustment with `Note` and no `ParcelId` (`chk_LedgerEntry_Parcel`; `UX_LedgerEntry_Parcel_Kind` is filtered to rows
  with a parcel); `Merchants.Merchant.PayoutHold` (2026-10-07).
- `Parcels.Parcel` keeps the weight rule it was booked under (`IncludedWeightGrams`, `BaseCharge`, `ExtraKgCharge`)
  and what a hub's scale read (`MeasuredWeightGrams`, NULL until weighed); `Scripts/Pre/002_ParcelWeightRule.sql`
  backfills the three from each courier's rate card before the publish.
- `Payments.OnlinePayment` (2026-10-08): a merchant paying what it owes online, numbered from the sequence
  `Payments.OnlinePaymentNumber` (`PAY-100001`), with the random `TransactionId` the gateway knows it by (unique), the
  amount asked, status (1 started, 2 paid, 3 failed, 4 held for review, 5 refunded), what the gateway confirmed
  (`PaidAmount`, `StoreAmount` after its fee, `Method`, `ValidationId`, `BankTransactionId`) and `LedgerEntryId`, the
  one credit it wrote (`UX_OnlinePayment_LedgerEntry`; `chk_OnlinePayment_Credit`: paid exactly when credited).
  Merchant-filtered and in the policy.
- `Payments.PayoutRequest` (2026-10-08): a merchant asking to be paid now (status 1 open, 2 paid with `PayoutId`, 3
  refused with `Answer`, 4 withdrawn), the balance when it asked (`AmountAsked`), one open per business
  (`UX_PayoutRequest_Merchant_Open`). Merchant-filtered and in the policy.
- `Identity.StaffMember` (2026-10-12, 5.1): a staff login's hub (NULL for head office), job (TINYINT) and
  permissions (INT bit field), one per login (`UX_StaffMember_Tenant_User`), in the policy. `Scripts/2026/007_StaffJobs.sql`
  made every existing hub login relief staff at head office with every hub desk, which is what it could do before.
- Schemas: `Platform` (Tenant), `Identity` (User and Identity's tables, StaffMember), `Network` (Hub, Zone, Area), `Pricing` (DeliveryRate, DeliveryOption, DeliveryOptionCharge), `Merchants`
  (Merchant, MerchantApiKey, MerchantPicture, MerchantPayoutAccount, Moderator, PickupPoint), `Parcels` (Parcel,
  ParcelEvent, ParcelRequest, sequence TrackingNumber →
  `OD10000001`),
  `Delivery` (Rider, DeliveryRun, DeliveryAttempt, PickupRequest, VehicleCapacity, ReturnList, ReturnListParcel,
  sequence ReturnListNumber → `RL-100001`), `Payments` (LedgerEntry, Payout, PayoutRequest, OnlinePayment, sequences
  PayoutNumber → `INV-000001` and OnlinePaymentNumber → `PAY-100001`), `Notifications` (OutboxMessage).
- Every tenant table has `TenantId` + FK + index, the housekeeping columns, and its predicates in the security policy
  `Platform.TenantIsolation` (29 tables).
- Seed (`001_SeedCourier.sql`, then `002`, `003`): OneDrop Courier (Asia/Dhaka, BDT, SMS sender `OneDrop`, hotline 09610-001122,
  10 businesses per merchant account, 3
  attempts); 16 hubs (Dhaka city 5; suburbs Savar, Gazipur, Narayanganj; Chattogram, Sylhet, Rajshahi, Khulna,
  Barishal, Rangpur, Mymensingh, Cumilla), 21 zones, 87 areas; the rate card above. Demo logins, merchants, riders and (in Development)
  about twenty sample parcels come from `DemoDataSeeder` / `DemoActivity` at app start.

---

## 4. Decisions and the reasons for them

| Decision | Why |
|---|---|
| **2026-10-04: pivot to a traditional courier** (owner): no grouping, one parcel = one delivery; charges by service area and weight; COD charge; next-day payouts | The owner wants the product to work like Bangladesh's couriers (Steadfast) |
| Start the rebuild from the last commit (`3b48bc9`); the uncommitted Week 5 rewrite stashed (owner) | Clean base; nothing lost |
| Keep the multi-tenant base and all three isolation layers, seed one courier (owner) | The isolation work is sound and lets a partner courier be added later without a rewrite |
| Hub pages ask which hub first; the admin's dashboard stays courier-wide (owner's review, 2026-10-04) | Staff work at one hub, but an admin locked to one hub would miss work waiting at the others |
| 2026-10-12: the hub board shows every hub at once until a hub is chosen, for admins; "All hubs" is the first choice in the board's hub menu (owner) | In real life every hub has work waiting, so a chooser of hub cards told the admin nothing; one board summed over the courier, with the hub beside each item, does. Hub staff keep choosing first because they work at one hub, and pages that act at a hub (scan, assign, closing) still need one |
| 2026-10-12: the courier's admin keeps its name, hotline, SMS sender, attempts, businesses per account and the day's times; the address name, time zone and currency stay the platform's (Phase 4.3) | These were only in the database, so changing the hotline needed a developer. The three left out move what is already on its way: the address is in every merchant's bookmarks and API settings, and the time zone and currency decide which day a parcel, run or payout belongs to and what its money means. Lowering attempts or businesses applies from then on: a parcel keeps the tries it has had, a business is never removed |
| 2026-10-12: a parcel is stuck when it has not *moved* for the courier's days, and a move is a change of status or place, stamped on save as `Parcel.MovedOn` (Phase 4.6) | The history could not say it: the same status is written both for real moves (a returning parcel reaching the next hub) and for notes (a flag, a weighing, a cash change), and the last edit (`UpdatedOn`) moves with every note. Stamping in the save interceptor needs no clock threaded through every move of the parcel. On hold counts from its day, because a recipient who asked for next week is waiting, not stuck; out for delivery is left out because every run is closed the same day |
| Reset both databases and replace the DbUp history with one new seed (owner) | The old schema and data were the grouping product's; a fresh start is simpler than migrating |
| Recipients have no accounts: public tracking by code and SMS only; the phone sign-in is gone (owner) | That is how couriers work; the tracking code is the key |
| Service area from the pickup and destination zones (`Zone.City`, `Zone.IsSuburb`) | Couriers price inside city, suburb and outside city; the zone already belongs to a city |
| Charges snapshot on the parcel at booking | A rate change must never reprice a parcel already booked or invoiced |
| COD charge rounded to whole taka, away from zero | Money shown and paid in whole taka; ৳12.50 → ৳13 is what couriers do |
| Merchants sign up and wait for approval; admins can add active merchants directly | Couriers vet merchants before collecting cash for them |
| Pickups are assigned to a rider by the hub; a merchant may also drop parcels at a hub (`Pending` → `AtHub`). Until 2026-10-12 the merchant asked for each pickup; now they are scheduled as parcels are booked, one visit to each point a day (below) | Both happen in real life |
| A rider's day is a `DeliveryRun` per rider per day; each hand-over is a `DeliveryAttempt` | The run carries the cash check; attempts count towards the courier's maximum |
| On hold only while attempts remain; the last failed attempt returns the parcel | Couriers try three times, then return |
| Returns travel back to the pickup hub and are handed back there; the merchant pays delivery + return charge | The pickup hub is where the merchant's parcels come from |
| Payouts hourly, every line up to yesterday, idempotency key per payout; lines waiting when charges exceed cash | Next-day payout must not slip; a charge is carried, never lost |
| Public tracking shows each step by its status (a hold keeps its reason), never notes, address, phone or amounts | Notes can hold amounts or reasons meant for the merchant; a code is easy to pass around |
| Fraud check across merchants returns counts only | Merchants value shared refusal data; no merchant learns another's customers |
| Schema owned by a SQL project + DbUp, no EF migrations | The owner's DCN setup; `SchemaMatchesModelTests` catches drift |
| Hangfire owns its `HangFire` schema | Third-party tables upgraded by Hangfire itself |
| Jobs take the tenant as a parameter, stored by name | A slow courier never holds up another; Hangfire cannot load generic methods |
| No product-name prefix in code | Owner's preference (DCN style) |
| No hard-coded business values: prices, attempts, time zone, sender name are data | Owner's rule; each courier states its own |
| Razor Pages (no SPA); custom CSS design system with tokens, a light sidebar shell (2026-10-06; dark until then), inline SVG icons | One deployment, host-only cookies per courier, a clean "courier software" look without a front-end build |
| Tenant from the subdomain (panels) or API key (API) | Host-only cookies keep couriers apart |
| API keys `od_{12-char prefix}_{32-char secret}`, only a SHA-256 hash stored | The prefix finds the key before the tenant is known |
| Outbox in the change's transaction; texts written at send time; webhooks in their own loop | A change never saves without its message; a slow merchant server never delays an SMS |
| Integration tests use `OneDrop-Test`; each test makes its own merchants and riders | Test data never touches dev; parallel tests never count each other's parcels |
| **2026-10-18: the `*.Local.json` files are committed** (owner, replacing "secrets only in git-ignored `*.Local.json`"). They hold the dev and test connection strings (`sa`), the Gmail app password and the SSLCommerz sandbox keys | The repository is private to the owner and the team, and the live site (updated by `git pull` through `Deploy-Iis.ps1`) then gets the mail and SSLCommerz settings with nothing to set by hand, so the merchant's "Pay online" button appears. The live IIS app pool's environment variables still override the file, so production keeps its own connection string. The price: a secret in git history cannot be taken back, so the repository must never go public as it is, and the Gmail app password should be rotated if it ever might have leaked |
| 2026-10-05: a rider with parcels, an open run or an assigned pickup cannot be stopped or moved to another hub | A stopped rider's app shows no work and a moved one hands cash in at the wrong hub, so that work would be stuck |
| 2026-10-05: a pickup point cannot move to another zone while parcels or a pickup wait there | Those parcels were priced, and the pickup sent to a hub, by the old zone |
| 2026-10-05: a hold's "deliver on" day must be after today | A past day would put the parcel straight back in the queue as if the customer had asked for it |
| 2026-10-05 UI: success is a toast that goes by itself, a problem stays on the page; anything hard to undo asks first (`data-confirm`, a styled dialog); motion only on first paint (`html.js-enter`) and never for reduced-motion users | People read problems, not confirmations; live dashboards must not jump on every update |
| 2026-10-06 UI (owner, from Steadfast's panel): a favourites bar on every desk panel that each person fills with the menu's stars (up to eight, in a cookie per login, so it is drawn with the page and needs no schema change; it does not follow a person to another browser), and an account menu behind the avatar with a new change-password page; a white sidebar with the signed-in account on top and sign out in red; the page title and the hotline (tap to call) in the top bar; larger type and rounder cards; number cards read icon and label, then the number, then context; the merchant dashboard is "Right now" (each stage with its count and cash), quick actions, tools and links, the last 30 days, the week and the money; Payments opens with the next payout beside how it adds up | The owner found Steadfast's panel easier to use; the layout and spacing were taken, the colours were not (sky blue stays the one accent). Not taken: several businesses per login, a Bangla switch and a dark theme, which are product work rather than layout |
| **2026-10-06: several businesses in one merchant account** (owner, after Steadfast). Each business is a merchant row of its own; an added one points at its account's main profile (`Merchant.MainMerchantId`). The login's claims list its other businesses and a cookie per login (`business-{userId}`) picks the one worked in, which `ICurrentUser.MerchantId` returns; approval and the payout account are the account's, set on the main profile and followed by every business (`FollowAccount`), from the merchant's settings or the admin's page | Every query already keeps a merchant to `ICurrentUser.MerchantId`, so a business per merchant row gives each its own parcels, pickups, pickup points, balance, payouts, API keys and webhook without touching any of them. A cookie that names a business outside the login's own list is ignored, so it cannot open someone else's. Up to `Tenant.MaxBusinessesPerAccount` businesses besides the main profile (10 for OneDrop Courier, as Steadfast; NULL for none), owner's choice; a business has no login of its own, by the owner's choice: the one login works in all of them |
| 2026-10-05 brand (owner): a new logo, a drop-shaped map pin holding a taped parcel and landing on a ripple, on a sky blue tile; colour kept to ink on cool light grey paper with one accent, sky blue `#7cc6f2` (`--accent`; `--accent-text` `#0b6a9e` for text), flat fills, no gradients or glows; display headings in Bricolage Grotesque | The green made the product look like a copy of Steadfast; ultraviolet was rejected as looking AI-generated and marigold as too yellow; the owner asked for something light like sky blue. One exact door for each parcel is the name's promise |
| **2026-10-05: a hub weighs a parcel and the charge follows its scale** (`Parcel.Reweigh`); the parcel keeps the weight rule it was booked under (`IncludedWeightGrams`, `BaseCharge`, `ExtraKgCharge`) and is repriced on that, never on today's rate card | Merchants under-declare weight, and the charge was frozen at whatever they typed: a 5 kg parcel booked as 500 g travelled for ৳60 for ever. Couriers weigh at the hub and bill the real weight. Repricing from the current rate card would have broken the snapshot rule, so the rule itself is snapshotted |
| 2026-10-05 front page: drawn, looping animations (line art, ink and sky blue) only on the public front page; they rest while off screen and stay still for reduced-motion users | The owner asked for a cartoon animation like Steadfast's, but not a copy: a little planet instead of their road strip |
| **2026-10-06: the coverage map stays seeded but the admin can change it** (owner): hubs, zones and areas are added, edited and taken off on `/Admin/Coverage`; nothing is deleted, only archived, and a part of the map with work still on it refuses to move | The seed is the fastest way to launch a courier with 16 hubs and 87 areas, but a courier that cannot open a hub or add an area without a developer is not usable. Deleting would orphan parcels that were priced and routed by a zone, so what leaves the map is archived and the rows stay; the guards are the same idea as the rider and pickup-point ones (2026-10-05): nothing moves out from under work already in flight |
| **2026-10-06: email, not SMS, is the real channel** (owner: "as we cant intigrate real SMS lets use mail insted"). A real SMTP sender (Gmail with an app password, in the git-ignored `appsettings.Local.json`) carries the merchant notifications; recipients' texts stay on the fake SMS sender. The payout email is raised by `Payout.MarkPaid` as a domain event and goes through the outbox in a loop of its own | An SMS gateway in Bangladesh needs a contract the owner does not have yet, while a mailbox is free and merchants all have one. Merchants are the people our notifications are for; recipients are the ones who need SMS, and they gave us no email address, so there is nothing to send to until either a gateway or a recipient email field exists. Going through the outbox means a payout email is written in the same transaction as the payout, is retried, and never blocks the money |
| 2026-10-06: the payout gateways (bKash, Nagad, bank) and the app's own SQL login are left alone (owner: "we still dont have those so leave them") | Neither can be built without something the owner does not yet have: merchant accounts with the payment providers, and a decision about the shared SQL Server |
| **2026-10-06: moderators, people inside a merchant account with a sign-in of their own** (owner, from Steadfast). A moderator is an ordinary merchant login plus a `Merchants.Moderator` row saying what they may do; the row hangs off the account's main profile, not off a business. Pages are held by a filter over a list of page to permission (`Web/Authentication/MerchantPages.cs`), and a merchant page not on that list is the owner's alone. No email is sent: the owner is shown a first password once and hands it over | Shops are run by more than one person, and today they all share the owner's password, which also hands over the payout account and the API keys. A login plus a permission row needed no change to any query, because every merchant query already keeps to `ICurrentUser.MerchantId` and the account's main profile is what a merchant login already carries. Default-deny on the page list means a page added later cannot quietly open itself to moderators. Email is the one piece missing (there is no sender yet), so the password is handed over by the owner |
| **2026-10-07: problems a hub flags are a flag beside the status, not new statuses** (Phase 3.2, from Steadfast's "in review" and "exceptional" tabs). `Parcel.Issue` is in review or exceptional with what is wrong; it is raised only while the courier has the parcel, keeps the time it was first raised when changed, and is cleared with what was done. A flagged parcel is not handed to a rider; everything else it can still do. Partly delivered becomes a tab of its own, and the dashboards count it apart from delivered (the delivery rate still counts both) | A problem is not a stage of the journey: a damaged parcel can be at a hub, on its way back or even delivered, and the state machine, the webhooks and every count by status stay as they were. Keeping it from a rider is the one guard that matters, because the door is where a parcel under review must not go. Pending parcels are not flagged (the courier does not have them yet), so a cancelled parcel can never be left flagged. The flag tabs show only while they hold a parcel, so eleven tabs do not crowd a merchant who has none |
| **2026-10-07: returns go back to the merchant on a return list a rider carries, and the merchant confirms them** (Phase 3.4, from Steadfast's return lists). A list is one pickup point's returning parcels with one rider of the hub that collected them; the rider's hand-over is what returns and charges each parcel, and the merchant's confirmation afterwards is a signature, not a gate. A list the rider could not hand over closes, and its parcels come back through the hub's scan like any parcel a rider brings back | Merchants should not have to come to the hub for their returns, and a courier needs proof they arrived. Charging at the hand-over keeps the ledger's rule that a parcel is charged when it reaches its final status, and payouts never wait on a merchant who forgets to confirm; a note on the confirmation is how a missing parcel reaches the courier. Reusing the hub's scan-in for parcels that did not go keeps one way back into a hub |
| **2026-10-07: changes to a parcel on its way are requests the courier answers** (Phase 3.5, from Steadfast). After pickup a merchant asks to cancel a parcel (approval sends it back as a return) or to change its cash on delivery (approval sets the new amount and the COD charge follows); the courier's admins approve or refuse with a reason. The merchant's own "Ask for it back" became this cancellation request | Once the courier has a parcel it has spent on it and may have it on a van or with a rider, so it decides; a request also leaves a record of who asked, why and what was answered. Approval applies the change through the parcel's own rules (a return, or a new cash amount with a history line), so an approved request can never put a parcel where the state machine would not. A cancellation is refused while a rider has the parcel rather than chasing the rider: it waits for the parcel to come back |
| **2026-10-07: several payout accounts, the one in use still on the main profile; and "get paid now"** (Phase 3.6, from Steadfast). Saved accounts are a table of the account's; choosing one copies it onto the main profile, which the businesses, the payout run, the admin and every payout already read. Get paid now makes the same payout the run makes, with today's lines too, and sends it at once | Copying the chosen account onto the main profile meant no change to how payouts are made or who reads the account, and keeps "where the money goes" in one place for every business. Paying now through the run's own rules (the ledger lines, the payout's idempotency key, the gateway, the email) means a merchant cannot be paid twice or paid what it does not have; there is no limit on how often, which a courier that wants one can add as a setting |
| **2026-10-07: the bell is worked out, not stored** (Phase 3.7). Its notices come from the parcels, attempts, return lists, requests and payouts of the last week when a page is drawn, and "seen" is the newest notice's time in a cookie per login | Every source already records what happened and when, so a notices table would be a second copy to keep in step with each flow. A week of a merchant's own rows is a few small indexed queries. The cookie, like the favourites, needs no schema change; it does not follow a person to another browser |
| **2026-10-07: the merchant's ID is shown** (Phase 3.8, owner's request from Steadfast) though "never show a raw id" is the rule: it is the business's id, shown as "Merchant ID" with a copy button for calls to the hotline, and the admin's merchant search finds it | A merchant reading out a number the courier can look up is quicker than spelling a shop name; it is the one id a person is meant to read, so the rule stays for everything else |
| **2026-10-07: payouts the admin can steer** (Phase 4.1, owner: "Lets fix these"). A refused transfer keeps the gateway's reason and is retried hourly; the admin sends it again or cancels it, which frees its lines. Holding is per account, with a reason. A merchant who owes the courier is settled by an adjustment line (no parcel, a reason), never by editing lines. Payouts still do not wait for a rider's cash | A payout copies the account it was made for, so a payout to a wrong account could never succeed: cancelling it is the only way to send the money to a corrected one. Its key stays the same on every retry, so a transfer the gateway made but failed to confirm is never made twice (the cancel dialog says to check with the gateway first). A ledger is only ever added to, so a settlement or a correction is a line of its own the merchant can read. The merchant is owed what the door collected; a rider short of cash is the courier's matter with its rider, made visible in 4.2 and 4.7 rather than taken from the merchant |
| 2026-10-07: the admin's bell is a list of what waits, not of events (Phase 4.2): one line per kind of work with its count, timed by its newest item | An admin acts on work that is open, not on a feed; counting what waits keeps the bell short however busy the courier is, and each line's time still tells them when something new joined it |
| **2026-10-08: SSLCommerz (the owner's sandbox store) takes one kind of money in: a merchant paying what it owes.** Not the payout gateway, and no prepaid wallet; paying a recipient's COD online comes next, with refunds (owner, unsure which use; this was the recommendation). The payment credits an `Adjustment` line, and is believed only when our server asks the gateway; a held one (risky, or another amount) waits for the admin; a check job catches payers whose browser never came back | SSLCommerz collects payments; it cannot send money to merchants' bKash accounts, so payouts stay fake. A prepaid wallet would fight the ledger: the hourly payout run pays any positive balance back the next morning, so a top-up would be returned. Paying a debt is the one use the ledger already understands; a credit adjustment is what 4.1 already writes when a merchant pays by hand, so statements, invoices, payouts and reports needed no change. The return address is open to anyone, because the merchant's sign-in does not travel with the gateway's post, so nothing posted is believed. Money the gateway took is always credited, once (the payment's row version and a unique credit), even after the page said it failed, because double-charging a merchant is worse than a late credit. Localhost cannot receive SSLCommerz's notice, hence the job |
| **2026-10-12: recipients pay the COD online** (4.10; the owner asked to "complete all the logics from phase 4", which this was waiting on). The payment is an `OnlinePayment` with a `ParcelId`; the parcel takes it as `PaidOnline`, the rider collects ৳0, and the merchant is paid through the parcel's ordinary lines. Refunds go out by themselves through SSLCommerz's refund API. The gateway's fee is the courier's cost | Reusing the merchant payment's settlement (ask the gateway, credit once, the check job) kept one proven path for money in. Writing no line of its own means the merchant's Cod line, COD charge, payouts and reports need no change and cannot count the money twice; the rider's cash stays on the attempt, so closing a run expects ৳0 for a paid parcel. Asking for the parcel's phone before showing the amount keeps tracking's rule that a code alone shows no money. Paid parcels cannot be partly delivered and their COD is frozen, because a partial refund or a changed amount would need a refund flow per change; refused at the door, the whole payment goes back. Refunds are owed rather than sent at once from every flow that ends a parcel, so one job owns them and a gateway outage only delays them |
| 2026-10-12: staff logins are Identity's roles, managed on `/Admin/Staff`, with no table of their own (4.4); replaced the same day by 5.1 below | A page over the existing roles gave the admin what 4.4 asked for before Phase 5's model was decided. The two guards (not yourself, not the last admin) stop a courier from locking itself out, and stay |
| **2026-10-12: staff have a job, a place and permissions** (Phase 5.1, owner: "I want to have role based staff authorization"; the owner took the four recommendations). A `StaffMember` row per staff login: one hub or head office (every hub), a job that presets permissions, and ticks on top; one role (`HubStaff`) for every staff login and `TenantAdmin` for super admins. Pages and buttons are default-deny against a page list; a hub's staff see only their hub; head office work is never given at a hub. A super admin manages everyone and alone makes super admins; a hub manager manages their own hub's people; nobody gives what they do not hold. Existing hub logins became relief staff with every hub desk | Real hubs split the work by desk (receiving, line-haul, delivery, returns, cash) and head office by department, and people cover each other, so a preset plus ticks fits better than fixed roles. Permissions in a row, not in Identity roles, need no role table change, are read on every request (a change applies at once) and copy the moderators' proven model (a bit field, a default-deny page list), so a new page cannot open itself to someone by accident. Locking a hub's staff in the one list every hub page finds its hub in makes another hub's code simply not found, with no check per page. "Never more than you hold" lets a hub manager hire without becoming a way round the super admin. Making old logins relief staff kept everyone's work going on the day it shipped. Sorting waits for racks (5.3), so it is not a job yet |
| **2026-10-08: "Get paid now" became a request the admin approves** (owner, after asking "if there is a get paid now then everybody will take out the money"; offered a rules-based instant payout, a request, or removing it, the owner chose the request). The merchant asks, once at a time; the admin pays at once or refuses with a reason; the daily payouts are unchanged | Paying instantly had three holes: today's cash may still be in a rider's pocket (a parcel counts as delivered when the rider taps it, the cash arrives when the run is closed), nothing was kept back for parcels on their way that may come back and be charged, and nothing limited how often. A person deciding, shown exactly those three facts, closes them without rules the courier would have to tune. The admin's own "Pay now" on a merchant's page stays |
| **2026-10-12: delivery types, a map pin, and one pickup a day** (owner). Speed (regular, express) and what is inside (general, liquid, fragile, fresh food, frozen) are two choices, so a parcel can be express and fragile; each is an extra on the rate card per service area, empty where not offered, snapshot on the parcel and part of the delivery charge. The pin uses Google Maps (owner's choice) with a key the owner adds; without one, a pasted link still pins and riders still navigate. Pickups are scheduled as parcels are booked and never asked for (by a 3:00 pm cut-off at first, replaced on 2026-10-13: see below); the merchant's pickup request is gone | The owner wanted Steadfast-style delivery types with fragile above regular and frozen above fragile; two choices avoid a type per combination, and extras keep the ledger, invoices and reports unchanged. Fresh and frozen food cannot wait days on the road, hence not outside the city. One daily truck at 3:30 pm is how the owner runs pickups; scheduling at booking plus a catch-up job means a missed or partial pickup is never forgotten. Tenant settings are nullable because the seed scripts that insert the tenant cannot change, so a NULL cut-off means "collected the day it is booked" |
| **2026-10-13: only a pickup van collects, and every load is checked against what the vehicle already carries** (owner: a bicycle rider with a 15 kg limit was being given more than that, and "the Cycle and Bike delivery rider will only take the parcel to the Customer but they wont pick up parcel form the Marchant"). `Vehicle.CollectsPickups` is true for the van alone; a collected parcel keeps the van's `RiderId` until the hub scans it in; `Fleet.Collect` weighs a pickup against the van's current load, not against an empty vehicle; delivery runs and return lists check the same load, at the weighed weight | The limits were real but the arithmetic started from zero every time: a pickup was sized against an *empty* vehicle, so a bicycle whose limit is 15 kg was handed a 25.5 kg pickup because nothing asked what it was already carrying, and once collected the parcels were nowhere (no hub, no rider), so the next pickup could not see them either. Keeping the parcel on the van until the hub scans it in makes the van's load a fact the next decision can read, and costs no column: `RiderId` was already there, already in the location check, and already cleared by `ReceiveAt`. Restricting collection to the van matches how couriers run (one truck leaves the hub at 3:30 pm for every merchant on the round; a rider on a bike cannot carry a shop's day of parcels) and makes "which vehicle" a question the hub never has to answer again. Return lists were the one load nothing checked at all. Weighing at the scale's reading rather than the merchant's figure matters because under-declaring is routine (2026-10-05), and the limit that protects a rider must use the real weight |
| **2026-10-13: the van takes what is waiting when it reaches a merchant, and visits each pickup point once a day** (owner: "the pickup truck will start the picking up at 3:30 … and if the merchant adds a new order at the last minute before the pickup guy arrives at the door the pickup guy will also take that. But if the pickup guy already visited that merchant and after that the merchant added a new order, that order will be picked up the next day"). This replaces the 3:00 pm cut-off of 2026-10-12: `Tenant.PickupCutoff` is gone, a point the van has completed today sends new bookings to tomorrow's pickup, and a pickup dated tomorrow cannot be given to a rider today | The cut-off was a clock rule standing in for what actually happens at the door: a parcel booked at 3:10 pm waited a day even when the van reached the merchant after 4 pm. The rule that matters is the visit, and the app already records it (the rider completing the pickup), so a merchant is never left waiting for a van that has gone and the van never goes to a merchant twice. A booking is listed for the day at once, as before, so the hub sees tomorrow's work as soon as the van has been. The "once a day" is kept in code, not a unique index, because older databases hold several completed pickups for one point and day. The merchant is told which day a parcel goes on the booking form, the parcel page, the dashboard and Pickups |
| **2026-10-13: delivery types are the admin's** (owner: "make what is inside and delivery type dynamic so that admin can change or add new one"). Each speed and kind of contents is a `Pricing.DeliveryOption` row with its extra per service area in `Pricing.DeliveryOptionCharge`; the parcel points at its options and keeps the extras it was booked at. Each kind has one default (regular, general goods: everywhere, free, cannot be taken off). The API and CSV name an option by a code made from its first name that never changes, or by its name. "Goes first" and the handling note are the option's, read as they are now | Fixed enums meant a developer for every new type and for every rename, against the rule that business values are courier data. The extras stay on the parcel, so a price change or a removed option never reaches a parcel already booked, invoiced or paid out; the name and handling note are read live because they describe the parcel rather than bill it. A fixed code keeps a merchant's integration working when the admin renames an option, and matching names in any spelling keeps old calls (`freshFood`) working. A default per kind keeps "choose nothing" meaning something on every route. Rows are archived, never deleted, because parcels point at them |
| **2026-10-08: ready to go live, for the owner's portfolio** (owner: "make it ready to be live. Remove if there is any demo data"; the hosting and the address are the owner's senior's to choose). A live site starts clean: the courier, its map and rates from the database scripts, and one admin made from `Setup:AdminEmail` / `Setup:AdminPassword` when missing and never changed once it exists. `deploy/` runs it on one Linux server (SQL Server Express, the app as Production trusting the proxy's forwarded headers, Caddy for HTTPS); `Tenancy:HomeCourierHost` serves the courier on one address. The dev database was left as it is (owner) | Demo logins with a password printed in the README cannot be on a public site, and a database deployed from nothing had nobody who could sign in, because only the Development demo seeder made users. A setting that only adds what is missing lets the password setting be emptied after the first sign-in. One address instead of `onedrop.<domain>` works on any host (a platform's own address, a subdomain of a company domain, sslip.io) without wildcard DNS. Without the forwarded headers behind a proxy the app saw http and the proxy's address, so cookies, links and the per-address rate limit were wrong. Express, because SQL Server's Developer edition may not run a live site |
| **2026-10-16: demo data is never made on a site that serves a courier at an address of its own** (owner: the live site opened with made-up merchants, riders and parcels its admin could not clear, and "i dont want him to do too much work … he will only run that one file"). `Program.cs` seeds demo data only when `Seed:DemoData` is on, the app is Development **and** neither `Tenancy:HomeCourierHost` nor `Tenancy:Domains` names an address; otherwise it logs why it did not. `Deploy-Iis.ps1` also sets the live app pool to Production with the demo seed off, and `Scripts/2026/014` empties what was already made | The live site was started as Development with the seeder on, so every start made the demo data again: emptying the database alone would have filled it back up on the next deploy, and the panel cannot clear it (a rider holding a parcel cannot be stopped, and the demo logins' password was not one the owner had). The environment is a setting on the server, which the owner cannot reach and did not want their teammate editing, so the app decides for itself from something only a live site has: an address it serves a courier at. A developer's machine has neither setting, so demo data still works there; an app pool or a compose file that names one never gets demo data, however its environment is set. The deploy's Production switch is what closes the `/Dev` pages (texts, emails, payouts, the test payment page), which Development had left open to anyone |
| **2026-10-16: a live start with no admin named makes the demo admin** (owner: the owner's teammate put the site live on IIS from the GitHub code and nobody could sign in, since the demo login exists only in Development and nothing named an admin: "when the project is live it automatically generates the super admin"). Outside Development, with `Setup:AdminEmail` empty, `Program.cs` makes the admin from `Setup:DefaultAdminEmail` / `Setup:DefaultAdminPassword` (`admin@onedrop.test` / `OneDrop#2026`, in `appsettings.json`) through the same `AdminSetup`, so it is created once and never changed. `deploy/docker-compose.yml` no longer requires `ADMIN_EMAIL`. Naming an admin (`ADMIN_EMAIL`) never falls back to it: with no password the start stops with the reason | A fresh server must always have someone who can sign in, and the owner wants the same login that works locally. The fallback is in the app, not in the compose file, so it holds on any host and cannot be reached by a half-filled `.env`: a named admin with no password would otherwise silently get a published password. It reverses the 2026-10-08 rule that no demo login belongs on a live site, for the portfolio's sake: the password is in the README, so anyone can sign in as a super admin until it is changed at `/Account/Password`, and the start logs a warning each time the fallback is used. A server that already names its own admin is untouched (it must empty `Setup__AdminEmail` and `Setup__AdminPassword`, in `.env` for Docker or on the IIS app pool, to get the demo admin as well). The first live start had left `admin@onedrop.test` existing with a password nobody had (and locked out by the guesses), which the fallback cannot touch, so `Scripts/2026/012_DemoAdminPassword.sql` gives that one login (a TenantAdmin of `onedrop`) the demo password once, on each database it runs on, and lets it back in; it creates no login, and a password changed afterwards stays changed because DbUp never runs a script twice |
| **2026-10-16: wrong passwords no longer lock a login** (owner: "Remove the too many attempt blocking", after the live site's super admin was locked by guesses). Sign-in calls `PasswordSignInAsync` with `lockoutOnFailure: false` and Identity's limit of five is gone. A login someone stopped is still refused: a stop is a lock with no end date (`IUserAccounts.SetLoginEnabledAsync`), which sign-in checks whatever the failure setting is, and it now says "This login has been stopped" instead of "Too many attempts" | The lock let anyone shut a known login out just by guessing at it, which is what happened to the live admin (its email and password are public), and it cost the owner a long wait. The price is that nothing now slows guessing at a password: sign-in has no per-address limit (only the tracking, tracking-payment and sign-up pages have one, 30 a minute). If protection is wanted back, a per-address throttle on sign-in that locks no account, like those, is the way, not a lock per login |

---

## 5. Environment

| Item | Value |
|---|---|
| Machine | Windows 11, .NET SDK 10.0.4xx, PowerShell 5.1 + Git Bash, Node 25 (for screenshot scripts) |
| SQL Server | `ras-x2,1433` (SQL Server 2025, login `sa`, shared with the team). From 2026-10-04 to 2026-10-06 it was reached as `10.50.0.1,1433` over the VPN while `ras-x2` did not resolve; the owner switched back to `ras-x2` on 2026-10-06 |
| Databases | `OneDrop` (development), `OneDrop-Test` (integration tests). **Ask before creating any other** |
| Connection strings | `src/Web/appsettings.Local.json` and `tests/Integration.Tests/testsettings.Local.json`, committed (2026-10-18) |
| SSLCommerz | The owner's sandbox store (2026-10-08; its merchant panel is `sandbox-gw.sslcommerz.com`, the API `sandbox.sslcommerz.com`). `Sslcommerz:StoreId` and `StorePassword` are in `src/Web/appsettings.Local.json`, which is committed, so the live site has them too; without them Development uses the test gateway (`/Dev/Pay`) |
| Tools | `sqlpackage` (dotnet global tool), `sqlcmd`, Docker Desktop, GitHub CLI at `C:\Program Files\GitHub CLI\gh.exe` |
| Repository | https://github.com/talha33177net-eng/OneDrop (private), local folder `C:\Courier Project`; the rebuild is on branch `traditional-courier` |
| Git identity | Talha Ahmed &lt;talha33177.net@gmail.com&gt; |
| App URLs | http://onedrop.localhost:5080 (the courier; the launch profile opens it); http://localhost:5080 sends visitors there (`Tenancy:HomeCourier`, 2026-10-08), and platform admins sign in at http://localhost:5080/Account/Login |
| Demo logins, API keys | [README](../README.md) — password `OneDrop#2026` (Development only, with `Seed:DemoData`) |
| Live site | Hosted on 2026-10-16 (morning) by the owner's teammate (ShafiurMiju) at `https://delyvo.octopi-digital.com`, **on IIS on Windows, not with the Docker `deploy/`**: the site "Delyvo" in `C:\inetpub\delyvo`, its settings as environment variables on the IIS app pool (not in any file; `ConnectionStrings__Database`, `Tenancy__Domains__…`), updated by running `.\Deploy-Iis.ps1` as administrator on the server (git pull, publish, `app_offline.htm`, `tools/db/publish.ps1`, copy, check); logs in `C:\inetpub\delyvo-data`. The Docker stack in `deploy/` was proved on this machine with Docker Desktop (`SITE_ADDRESS=localhost`) and is for any other Linux host. Work since the last push is not on the live site until it is committed, pushed and `Deploy-Iis.ps1` is run there |

### Everyday commands
```powershell
./tools/db/publish.ps1                           # update the dev database (after any schema or DbUp change)
./tools/db/publish.ps1 -Database OneDrop-Test    # update the test database (same trigger)
dotnet build Courier.sln
dotnet test --project tests/Domain.Tests
dotnet test --project tests/Architecture.Tests
dotnet test --project tests/Integration.Tests    # must report succeeded, not skipped
dotnet run --project src/Web
```

---

## 6. Working rules agreed with the owner

1. **Test every piece of work right after it is implemented** — build, new tests, republish databases if the schema
   changed, all three suites green, a live check in the running app; then update the plan's daily log.
2. **Follow DCN** (`C:\Git\DCN`) for conventions unless this project says otherwise.
3. **No product-name prefix** in projects, folders, namespaces or class names.
4. **Commits are authored by the repository owner only.** No co-author trailers, no "generated with" lines, no
   tool-named files in the repository.
5. **Secrets live in the committed `*.Local.json` files only** (2026-10-18). Never put one in any other file, a log or
   a commit message; the repository stays private.
6. **Ask before creating databases** on the shared server, and before anything that is hard to undo.
7. Leave changes uncommitted for review unless asked to commit or push.
8. Keep this file and the plan up to date as part of the work.
9. **Never hard-code business values**: they are courier data. Only the seed script and tests hold concrete numbers.

---

## 7. Traps already found (do not rediscover them)

| Trap | What to do |
|---|---|
| DbUp journals scripts by embedded resource name, which includes the project's root namespace | Never rename the namespace or folders of `Database Update` without updating `dbo.SchemaVersions` |
| `PRINT` without `;` before a `WITH` CTE in the post-deploy script fails the publish | End statements before a CTE with `;` |
| PowerShell 5.1 turns native stderr into terminating errors under `$ErrorActionPreference = 'Stop'` | Scripts use `Continue` and check `$LASTEXITCODE` |
| Piping `publish.ps1` into `Select-Object -First/-Last` stops it early (exit 255) | Log to a file and read the file |
| `WebApplicationFactory` starts its host lazily; parallel tests raced the seeders | The fixture starts the host once in `InitializeAsync` |
| The web app reads `appsettings.Local.json`; tests boot the web app | Tests override the connection in `ConfigureAppConfiguration`; tests only write to `OneDrop-Test` |
| xUnit v3 on .NET 10 needs Microsoft.Testing.Platform | `global.json` opts in; `dotnet test --project <path>`; filter with `-- --filter-class <name>` |
| An EF store-generated string with a non-null default skips the sequence | Tracking and payout numbers start as `null!` |
| `DATETIME2(0)` rounds | The interceptor truncates `Created` to whole seconds |
| The machine-wide NuGet config includes DCN's private feed | Repo `nuget.config` uses nuget.org only |
| curl keeps no cookies for `*.localhost` | Use `--resolve onedrop.localhost:5080:127.0.0.1` and pass cookies in a `Cookie` header |
| Microsoft.Build.Sql 2.2.0 breaks builds inside Visual Studio | Stay on 2.1.0 |
| SqlPackage blocks NULL → NOT NULL on a table with rows | Backfill and alter in a guarded `Scripts/Pre` script |
| `sqlcmd` runs with `QUOTED_IDENTIFIER OFF` | Pass `-I` when running scripts by hand |
| A running `dotnet run --project src/Web` locks `src/Web/bin` | Stop the app (`taskkill //F //IM Web.exe`) before rebuilding or testing |
| `dotnet run --no-build` after editing a file in `wwwroot` serves browsers an empty stylesheet or script: the asset list still names the old pre-compressed (gzip) copy, which no longer exists, and only a request asking for gzip gets it, so `curl` looks fine | Build (or plain `dotnet run`) after any `wwwroot` change; to test by hand, ask with `-H "Accept-Encoding: gzip"` |
| The app running on `OneDrop-Test` while the integration suite runs makes `FailedMessagesTests` fail: its outbox sender (every 5 s) handles the text the test plants before the test reads it | Stop any app pointed at `OneDrop-Test` before running the suite; live checks on it go before or after |
| A test that tries to write another tenant's rows really writes them when isolation is broken | Isolation tests write each row's own value back |
| Since Row-Level Security, SqlPackage refuses table rebuilds | `publish.ps1` passes `AllowUnsafeRowLevelSecurityDataMovement` (safe: its connection is unrestricted) |
| Hangfire cannot load a generic job method back | Job methods are never generic (jobs go by name) |
| MARS is on, so EF cannot use savepoints in the outbox transaction | `SavepointsDisabledBecauseOfMARS` is ignored |
| Save entities that raise events with `SaveChangesAsync` | The sync `SaveChanges` refuses them |
| EF cannot filter after a constructor projection, nor translate `group by` then `join` | Project with member initialisers; aggregate first, then look up names in a second query |
| `~/` links are fingerprinted by `MapStaticAssets` | Use a plain path for the web manifest and service worker |
| Razor HTML-encodes `৳` written inside a C# string | Use `Money.Taka(x)` (an `HtmlString`) or keep the symbol in markup |
| Plus Jakarta Sans (the UI font) has no ৳ glyph; Windows falls back to an odd one | `_Fonts.cshtml` loads Noto Sans Bengali for that one character (`text=%E0%A7%B3`) |
| `.mono` sets `font-size: .92em`, which shrinks an `h1` | `h1.mono` sets its own size |
| A grid container counts every child: a stray `<label>` or `<input>` takes a cell | Hidden helpers (`#nav-open`, `.nav-scrim`) are `display: none` outside the phone layout |
| Razor's encoder turns `+` and `=` into entities | Tests `WebUtility.HtmlDecode` pages before searching them |
| Public pages are rate limited per address, and all test requests share one | Each test `Visitor` sends `X-Test-Client` with an address of its own |
| Headless Chrome's `--window-size` is never narrower than its minimum | Use DevTools emulation (`Emulation.setDeviceMetricsOverride`, width 390, `mobile: true`) and compare `scrollWidth` with `clientWidth` |
| The VPN link to the SQL Server can be slow | Check `sys.dm_exec_sessions`; stop and rerun a stalled publish |
| A CSS rule beats an SVG presentation attribute (`stroke-width="5"` loses to `.o { stroke-width: 2.4 }`) | In the drawings, a one-off stroke or fill goes in `style="…"` |
| Headless screenshots do not move animations that run on the compositor (the planet's turn) | To see a frame, pause every animation at a time: `document.getAnimations().forEach(a => { a.pause(); a.currentTime = t; })` |
| Until 2026-10-16 the sign-in page listed every `.test` login ("Demo accounts") in Development; with the thousands in `OneDrop-Test` one GET of `/Account/Login` took about 50 s. The list was removed so the page is the same everywhere and nothing shows a password | Sign in with the email and password; the demo seed still makes the logins, but the page no longer lists them (see the README) |
| A short class name on a page can collide with a shared partial's (`.step` vs `.progress-steps .step`) | Front-page classes are prefixed (`how-step`, `hero-…`); search `Pages` before adding a bare one |

---

## 8. Glossary

| Term | Meaning |
|---|---|
| Tenant / courier | An operator (OneDrop Courier). Selected by subdomain or API key |
| Merchant | A shop that sends parcels. Sees only its own |
| Service area | Inside city, suburb or outside city: decides the price |
| Hub | A sorting point; each zone is served by one hub |
| Pickup hub / delivery hub | The hub of the pickup point's zone / of the recipient's area |
| Run | One rider's deliveries on one day, closed with the cash handed in |
| Attempt | One hand-over of a parcel to a rider and its outcome |
| COD | Cash on delivery, collected at the door and paid to the merchant the next day less charges |
| Payout | One payment to a merchant, with an `INV-` invoice of its ledger lines |
