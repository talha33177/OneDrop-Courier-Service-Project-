# Implementation plan and progress tracker

The one place to see where we are and what comes next for **OneDrop Courier**, the traditional courier system
(pickup, hubs, delivery, cash on delivery, next-day payouts). The grouped-delivery plan it replaces (2026-09-27 to
2026-10-04) is in git history up to commit `3b48bc9`.

**Start of every session:** read [Today](#today), then take the first unchecked task.
**End of every task:** run the [test routine](#test-routine-after-every-task), tick the box, add a line to the
[daily log](#daily-log).

---

## Today

| | |
|---|---|
| Current phase | **Phase 5** — staff jobs at the hubs and at head office (Phase 4 left: 4.8, the pass over every admin page) |
| Next task | The owner's review of 5.1 (staff jobs and permissions). Then 5.4 bags between hubs and 5.5 hub cash and deposits (recommended order), or 4.8. The owner's senior puts the site live with [Deployment.md](../Documentation/Deployment.md) (server and address are theirs to choose). Still waiting on the owner: review and commit of everything since `db63259` (uncommitted on `main`); 2.6 and 2.7 wait on accounts and a decision |
| Last session | 2026-10-13 — 4.16: the van visits each pickup point once a day and takes whatever is waiting when it gets there (no 3:00 pm cut-off). Before it the same day, 4.15 vehicle limits that hold and 4.14 admin delivery types; 2026-10-12, 5.1 role-based staff. **The dev database `OneDrop` still has `Platform.Tenant.PickupCutoff`** (the owner's app was running on it): run `./tools/db/publish.ps1` after restarting the app on the new build |
| Blockers | None |

---

## Test routine (after every task)

A task is **not done** until all of these pass. Record the result in the daily log.

1. **Build:** `dotnet build Courier.sln` shows 0 errors and no warnings.
2. **New tests:** every new rule gets a unit test in `tests/Domain.Tests`. Anything touching the database, the
   API, a page or tenancy gets an integration test in `tests/Integration.Tests` (and its line in the isolation sweep
   for a new route).
3. **Schema changed?** `./tools/db/publish.ps1` (dev) **and** `./tools/db/publish.ps1 -Database OneDrop-Test`.
4. **All suites green:**
   ```powershell
   dotnet test --project tests/Domain.Tests
   dotnet test --project tests/Architecture.Tests
   dotnet test --project tests/Integration.Tests     # must say succeeded, not skipped
   ```
5. **Live check:** `dotnet run --project src/Web`, then use the feature on http://onedrop.localhost:5080 at desktop and
   phone width. Check another merchant, rider or courier cannot see it.
6. **Update this file:** tick the task, note anything deferred, update [Today](#today).

---

## Phase 1 — The rebuild as a traditional courier ✅ (2026-10-04)

The owner's request: "Forget the group delivery system … make this project just like the traditional delivery
systems of Bangladesh like Steadfast … make the UI normal, minimal and good looking … make every user panel and the
admin panel better." Owner's answers: start from the last commit (stash the uncommitted work), keep the multi-tenant
base with one courier seeded, reset both databases, recipients track publicly with no accounts.

- [x] **1.1 Domain.** Grouping, customers, orders, trips, shelves, door payments, settlements, trust and drop-off rules
      removed. New: `Parcel` (state machine, location fields, charges snapshot, events), `ServiceArea` and
      `DeliveryRate`, `Merchant` (status, payout account), `PickupPoint`, `Rider`, `DeliveryRun`, `DeliveryAttempt`,
      `PickupRequest`, `LedgerEntry`, `Payout`; `PhoneNumber` moved to `Domain.Common`; `Tenant` keeps only name, slug,
      time zone, currency, SMS sender, hotline and maximum attempts.
- [x] **1.2 Schema and seed.** SQL project rewritten (schemas `Parcels`, `Pricing`; `Delivery`, `Payments`, `Network`,
      `Merchants` reshaped), sequences `TrackingNumber` and `PayoutNumber`, the security policy on all 17 tenant tables.
      DbUp history replaced by `001_SeedCourier.sql` (16 hubs, 21 zones, 87 areas, the rate card). Both databases
      dropped and deployed from nothing.
- [x] **1.3 Application.** Booking (API, form, CSV), browse and details, actions (edit, cancel, return), labels,
      tracking, quote, fraud check; hub scan, board, assign, runs; pickups; rider day; riders; merchant onboarding,
      admin, account; payouts job, merchant payments, admin payouts; rates; coverage; dashboards; outbox contracts,
      recipient texts, webhooks, failed messages.
- [x] **1.4 Infrastructure.** Mappings, `AppDbContext`, Identity without customers, tracking links, user accounts, fake
      payout gateway, the demo seeder (logins, merchants, riders, keys) and `DemoActivity` (sample parcels in every
      stage in Development), Hangfire job `merchant-payouts`.
- [x] **1.5 UI.** A new design system (`site.css`: tokens, dark sidebar shell, cards, stats, badges, tables, tabs,
      timeline, charts, public site, rider mobile, print labels) with inline SVG icons; public site, sign-in and
      sign-up; merchant, hub, rider, admin, platform and Dev panels rebuilt.
- [x] **1.6 API.** `POST/GET /api/v1/parcels`, `POST /api/v1/parcels/{code}/cancel`, `GET /api/v1/charge`,
      `GET /api/v1/areas`.
- [x] **1.7 Tests.** Domain tests rewritten (parcels, pricing, money, delivery, merchants, webhooks, outbox); integration
      tests rewritten (parcel API, merchant panel, delivery flows, payouts, webhooks, texts, failed messages, portal
      pages, simulator, isolation sweep over every route, wrong-host sweep, Row-Level Security, save interceptor).
- [x] **1.8 Simulator, docs.** Simulator books parcels; README, Project-Context, Architecture, Demo, Conventions,
      Database rewritten for the courier.

---

## Phase 2 — After the rebuild

- [ ] **2.1 Owner's review and commit** on `traditional-courier`; merge into `main` when approved.
- [x] **2.2 Coverage admin** (2026-10-06). `/Admin/Coverage` keeps the map it always showed and gains three tabs:
      hubs, zones (code, name, city, suburb, hub) and areas (name, zone, filtered by zone). The seed still lays the
      map out; a part of it that leaves is archived, never deleted, and refuses to move while work rests on it.
- [x] **2.3 Run sheet** (2026-10-06). `/Hub/RunSheet/{run}`: the parcels still to hand over with the door, the phone,
      the cash and a line to sign; offered when parcels are assigned and from the closing page. Printed from the page.
- [x] **2.4 Merchant notifications** (2026-10-06). The merchant is emailed when its payout has left: what was
      collected, the charges, what was sent, to which account, and a link to the invoice. Through the outbox.
- [x] **2.5 Reports** (2026-10-06). `/Admin/Reports` over a chosen period: day by day (delivered, returned, cash,
      charges, earned, owed), by rider (stops, deliveries, cash collected against handed in, short) and returns by
      merchant. Each table downloads as CSV.
- [~] **2.6 Real gateways.** **Email is real** (SMTP, Gmail today; `Email:*` in `appsettings.Local.json`) and sends
      the merchant notifications. **SMS and the bKash / Nagad / bank payout adapters stay fakes**: the owner has no
      accounts with those providers yet (2026-10-06), so recipients' texts are still listed at `/Dev/Sms`.
- [ ] **2.7 The app's own SQL login** without `ALTER ANY SECURITY POLICY` (shown to the owner before it is created).
      Left alone for now by the owner's choice (2026-10-06): the app still signs in as `sa`.

---

## Phase 3 — The gaps found in Steadfast's merchant panel (owner, 2026-10-06)

The owner walked through Steadfast's panel again and asked for the features we do not have. One task at a time, each
with the full test routine. Moderators first, by the owner's choice.

- [x] **3.1 Moderators** (2026-10-06). People who work inside a merchant account with their own sign-in and their own permissions
      (dashboard, parcels, payments, support, settings), invited by email with a code; the account owner chooses what
      each may see and do, and can stop them. New table, invitation flow, a permission check on every merchant page.
- [x] **3.2 More parcel tabs** (2026-10-07). Partly delivered is a tab of its own; hub staff and admins flag a parcel
      **in review** or **exceptional** with what is wrong (`Parcel.Issue`), clear it with what was done, and the
      flagged parcel stays off riders' runs meanwhile. The merchant sees the flag tabs while they hold a parcel, the
      flag on the parcel and on its dashboard, and in the API.
- [x] **3.3 Stats page** (2026-10-07). `/Merchant/Stats`: the parcels booked in the last 7 or 30 days, this month,
      last month or the merchant's own days (`Domain.Common.Period`, at most a year): booked with their cash, the
      delivery, return and cancel rates, cash collected, where they are now as one bar, and count, cash and share per
      status with a link to each status's parcels for those days.
- [x] **3.4 Returns the merchant signs for** (2026-10-07). `/Hub/Returns` sends a pickup point's returning parcels
      back with a rider on a return list (`RL-100001`); the rider records the hand-over (each parcel returned and
      charged) or why not on `/Rider/Return/{number}`; the merchant follows `/Merchant/Returns` and confirms a list on
      `/Merchant/Return/{number}`, with a note when something is missing.
- [x] **3.5 Cancellation and amount-change requests** (2026-10-07). From a parcel on its way the merchant asks to
      cancel it or change its cash (`Parcels.ParcelRequest`); admins approve (the parcel changes at once) or refuse
      with a reason on `/Admin/Requests`; the merchant sees answers on the parcel and `/Merchant/Requests`. Replaces
      the merchant's direct "Ask for it back".
- [x] **3.6 Payment request and several payout accounts** (2026-10-07). Several saved accounts
      (`Merchants.MerchantPayoutAccount`), one in use; "Get paid now" on Payments pays every unpaid line at once,
      today's too; the balance splits into ready, today and still to collect.
- [x] **3.7 Notifications in the panel** (2026-10-07). A bell in the merchant's top bar with the last week's
      deliveries, holds, refusals, returns, flags, return lists, answered requests and payouts; worked out, not stored.
- [x] **3.8 Small things from the same panel** (2026-10-07). `/Merchant/Export` (full CSV, up to 10,000),
      `/Merchant/ByDate`, the merchant ID with a copy button (account menu, Settings, admin search), and empty lists
      that offer their next action.

Not taken from Steadfast: a prepaid wallet ("Add fund", "Check balance"), a different money model from our COD ledger
that needs a real gateway (2.6); Pick-n-Drop and "My incoming", which are another service line; and the Bangla
switch, which the owner parked on 2026-10-06.

---

## Phase 4 — The admin panel and payouts the courier can steer (owner, 2026-10-07)

The owner asked whether merchant payouts work (they do: every payout equals its lines, no line paid twice) and what
the admin panel is missing, then "Lets fix these". Payouts come first, because the admin's bell needs a stuck payout
to say why it is stuck.

- [x] **4.1 Payouts the admin can steer** (2026-10-07). A failed transfer keeps why, how often and when it was tried, and the admin
      sends it again or cancels it (its lines are freed for a new payout to the account as it is now). Hold or pay one
      merchant. Merchants who owe the courier listed apart, and an adjustment line (the merchant paid us, or a credit
      or charge with a reason) to settle them. A short "how payouts work" box, and when the hourly run last ran and runs
      next.
- [x] **4.2 A bell for the admin** (2026-10-07). Sign-ups waiting, requests to answer, flagged parcels, failed messages, stuck
      payouts, riders short of cash, notes on return lists, merchants owed money with no payout account.
- [x] **4.3 Courier settings** (2026-10-12). `/Admin/Settings`: name, hotline, SMS sender, delivery attempts,
      businesses per account, pickup cut-off (dropped in 4.16) and time, riders due back, days before a parcel is stuck.
- [x] **4.4 Staff accounts** (2026-10-12). `/Admin/Staff`: add an admin or hub staff with a first password shown once,
      rename, change role, stop, let back in, new password; nobody stops or demotes themselves and the last admin who
      can sign in stays one. Identity's roles only, no new table: 5.1 (a hub and permissions per person) builds on it.
- [x] **4.5 The whole merchant for the admin** (2026-10-12). Its businesses, moderators, payout accounts, payouts,
      requests and return rate on `/Admin/Merchant/{id}`.
- [x] **4.6 Stuck parcels** (2026-10-12). `/Admin/Stuck`, a bell line and a menu mark; a move is a change of status or
      place (`Parcel.MovedOn`); the days are the courier's setting.
- [x] **4.7 A page per rider** (2026-10-12). `/Admin/Rider/{id}`: runs, deliveries, cash collected against handed in.
- [ ] **4.8 A pass over every admin page** at desktop and phone width, fixing what is broken.
- [x] **4.9 Merchants pay what they owe online, through SSLCommerz** (2026-10-08, owner's sandbox store). "Pay ৳X
      online" on Payments when the charges come to more than the cash; the gateway is asked before anything is
      credited, one credit per payment, a check job for payers who never came back, risky payments held for the
      admin. A test gateway (`/Dev/Pay`) in Development without a store. Proved on the owner's real sandbox store:
      a bKash payment credited, a "success with risk" Nagad payment held and then credited by the admin.
- [x] **4.10 Recipients pay their COD online** (2026-10-12, the owner's "complete all the logics from phase 4"). "Pay
      online" on the tracking page opens `/Track/Pay/{code}`, which names the amount only for the parcel's own phone;
      the payment (`OnlinePayment.ParcelId`) is believed only when the gateway confirms it, the parcel takes it once
      (`Parcel.PaidOnline`), the rider collects ৳0 and the merchant is paid the whole COD with the parcel. A payment the
      parcel cannot take, or for a parcel cancelled or returned, is refunded through the gateway by the check job.
- [x] **4.11 "Get paid now" becomes a request the admin approves** (2026-10-08, owner's choice). "Ask to be paid now"
      on Payments, one open request per business, taken back by the merchant if it likes; the admin sees the balance,
      today's part, the cash still with riders and the charges if the parcels on the way came back, then pays at once
      or refuses with a reason the merchant reads.

- [x] **4.12 Ready to go live** (2026-10-08, owner: for the portfolio, no demo data). A live site starts with the
      courier's setup and one admin made from `Setup:AdminEmail` / `Setup:AdminPassword` (never changed once it
      exists); `Tenancy:HomeCourierHost` serves the courier on one address; `deploy/` runs SQL Server Express, the app
      as Production behind Caddy (HTTPS); [Deployment.md](../Documentation/Deployment.md) for the owner's senior. The
      dev database was left as it is.

- [x] **4.13 Delivery types, the door on a map, one pickup a day** (2026-10-12, owner). Express and what is inside
      (liquid, fragile, fresh food, frozen) as extras on the rate card per service area; the recipient's door pinned on
      Google Maps (with a key) or by a pasted link, and Navigate for riders; pickups scheduled by the 3:00 pm cut-off
      (truck at 3:30 pm; the cut-off was replaced in 4.16) with the merchant's pickup request removed; "Manage
      businesses" in the merchant menu.

- [x] **4.14 Delivery types the admin keeps** (2026-10-13, owner). Speeds and kinds of contents are rows
      (`Pricing.DeliveryOption`, extras per service area in `Pricing.DeliveryOptionCharge`) the admin adds, prices,
      orders, makes the default and stops offering on `/Admin/DeliveryTypes`; parcels point at their options and keep
      their extras; the API and CSV take an option's code or name.

- [x] **4.15 Vehicle limits that hold, and pickups only by van** (2026-10-13, owner: a bicycle rider with a 15 kg limit
      was given a 25.5 kg pickup; and "the Cycle and Bike delivery rider will only take the parcel to the Customer but
      they wont pick up parcel form the Marchant"). `Vehicle.CollectsPickups` (the van alone); a collected parcel keeps
      the van's `RiderId` until the hub scans it in; `RiderLoads` adds up what a rider holds and is on the way to
      collect; pickups, delivery runs and return lists are all checked against it, at the weighed weight. `/Hub/Pickups`
      offers vans only with a load meter each; `/Hub/Assign` marks a parcel too heavy for any vehicle of the hub;
      `/Admin/Riders` and the dashboard checklist ask for a van per hub. `Scripts/2026/009` frees pickups that had been
      given to a bicycle or motorbike.

- [x] **4.16 One visit a day to each pickup point, no cut-off** (2026-10-13, owner: "the pickup truck will start the
      picking up at 3:30 … if the merchant adds a new order at the last minute before the pickup guy arrives at the
      door the pickup guy will also take that. But if the pickup guy already visited that merchant and after that the
      merchant added a new order, that order will be picked up the next day"). `Tenant.PickupCutoff` is gone (setting,
      column, `Scripts/Pre/005`); a parcel goes with today's pickup until the van has completed the point's pickup
      today (`PickupVisits.MadeToday`), then with tomorrow's, which is listed at once; the rider collects whatever
      waits at the point, and what they leave goes with tomorrow's; a pickup dated tomorrow cannot be assigned or
      collected today; the `pickups` job also calls off every pickup with nothing waiting. The rider's form posts the
      codes it offered, so a parcel booked while they were at the door refuses the pickup and is put on the list
      instead of being pushed to tomorrow. Booking form, parcel page, dashboard, merchant Pickups and the hub list say
      which day a parcel goes.

Kept as it is, by decision: payouts do not wait for a rider's cash. The merchant is paid what the door collected and
a short rider is the courier's matter with its rider; 4.2 and 4.7 make the shortfall impossible to miss.

---

## Phase 5 — Staff jobs at the hubs and at head office (owner, 2026-10-12)

The owner asked whether the admin panel has employee roles and how hub work is split in real life: some staff only
receive, some sort parcels onto racks by destination, some scan parcels out to other hubs, some handle returns, some
keep the accounts. Today the courier's people have two roles: `TenantAdmin` (everything) and `HubStaff` (one login
that can do every hub job at every hub). Nothing ties a person to a hub or a job, and the panel cannot make staff
logins (4.4). Approved with the owner's answers below; 5.1 is done.

**The model.** A staff member has *where* (one hub, or head office = every hub) and *what* (permissions, a bit field
like `MerchantPermissions`, held by a default-deny page list like `MerchantPages.cs`). Job titles are presets that
tick the boxes; the admin can tick more, because people cover each other's desks. Staff of a hub act only at that
hub: no hub menu, and the server refuses another hub's code.

| Job (hub) | Real-life work | In the app |
|---|---|---|
| Hub manager | Runs the hub, settles problems, covers any desk | Everything at their hub, the hub's staff activity, accepts a rider's cash shortfall |
| Receiving (inbound) | Unloads the pickup van and line-haul trucks, scans in, weighs | Receive scan, weigh, flag a problem |
| Sorting | Puts each parcel on the rack for its destination hub or delivery zone | Sort scan (5.3), find a parcel |
| Line-haul (outbound) | Bags parcels for other hubs, loads the truck | Dispatch scan, bags and manifests (5.4) |
| Delivery desk | Routes riders, prints run sheets, sends riders on pickups | Assign parcels and pickups, run sheets |
| Returns desk | Gathers returning parcels, return lists, counter hand-back | Returns, return lists, Hand back scan |
| Cash desk | Counts each rider's cash, deposits it in the bank | Close runs, cash deposits (5.5) |

| Job (head office) | In the app |
|---|---|
| Super admin (today's admin) | Everything, including staff, settings, rates, coverage; only one who makes super admins; the last one cannot be stopped |
| Operations manager | Every hub's board, parcels, flags, requests, riders, stuck parcels |
| Accounts / finance | Payouts, adjustments, online payments, payout requests, reports, hub cash deposits |
| Merchant support | Sign-ups, merchants, requests, fraud check |
| Customer support | Parcel lookup and history, read-only, for the hotline |

Riders and merchants stay as they are.

- [x] **5.1 Staff and permissions** (2026-10-12, absorbs 4.4). `Identity.StaffMember` per staff login (hub or head
      office, job, permissions), the job presets plus ticks, `/Admin/Staff` for super admins and head office (everyone
      below super admin) and hub managers (their hub), the page and button list for `/Hub` and `/Admin`
      (`StaffPages.cs`), the hub lock, menus, favourites, work marks, the bell and the dashboard by permission, the
      scanner offering only a person's modes, a home per job. Existing hub logins became relief staff. Sorting is not a
      job until racks exist (5.3).
- [ ] **5.2 Desk homes and who did it.** Each job signs in to its own desk (receiving to the Receive scanner, cash desk
      to closing); a parcel's history names the person ("Received at Mirpur by Karim", `ParcelEvent.UpdatedId` already
      holds it); the hub manager sees each person's scans per hour.
- [ ] **5.3 Racks and the sort scan.** Racks per hub (`B-2 → Chattogram`, `D-1 → Mirpur zone 10`, returns, problems);
      the Receive answer names the rack; the sorter scans the parcel then the rack's QR (a wrong rack beeps); a parcel
      shows its rack; the delivery desk can give a rider a whole rack.
- [ ] **5.4 Bags between hubs.** Line-haul scans parcels into a bag for a destination hub and seals it (`BG-100001`,
      a manifest, the truck); the receiving hub scans the bag, then each parcel; whatever is missing when the bag is
      closed is flagged exceptional with the hub and the person.
- [ ] **5.5 Hub cash and deposits.** Each hub's cash in hand (cash received at closing less deposits); the cash desk
      records each bank deposit (slip number, amount), finance confirms it; a shortfall needs the hub manager. Today a
      rider's cash stops at "run closed".

**The owner's answers (2026-10-12):** (1) one person may hold more than one job: a preset plus ticks; (2) the super admin
makes staff logins, and a hub manager for their own hub, never granting more than they hold; (3) one hub or head office
per person; (4) head office jobs come in the same round as the hub's. The order after 5.1 was not asked again; the
recommendation stands: 5.4 and 5.5, where real couriers lose parcels and cash, then 5.2 and 5.3.

---

## Background jobs

| When | Job |
|---|---|
| Every 5 seconds (`Jobs:OutboxInterval`) | Outbox senders in process, each in a loop of its own: recipient texts, merchants' webhooks, and merchants' emails |
| Every hour (`Jobs:Payouts`) | `merchant-payouts`: every merchant's ledger lines up to yesterday (courier's day) |
| Every 10 minutes (`Jobs:OnlinePayments`) | `online-payments`: asks the gateway about online payments still waiting or failed in the last day; credits what it confirms, closes what was never finished after a day; refunds recipients' payments owed back (a refused refund is asked again hourly) |
| Every 15 minutes (`Jobs:Pickups`) | `pickups`: carries a missed pickup to today, lists points whose parcels were left behind, calls off a closed list's pickups with nothing to collect |

## Must-pass tests

| Test | Where |
|---|---|
| Charges follow the rate card per service area and weight; COD charge rounded | `PricingTests`, `ParcelApiTests` |
| A parcel moves only along its state machine; at most one location | `ParcelTests` |
| The last failed attempt returns the parcel | `ParcelTests`, `DeliveryFlowTests` |
| A merchant sees only its own parcels; a rider only theirs; another courier nothing | `IsolationTests` (every route), `ParcelApiTests`, `RowLevelSecurityTests` |
| A payout pays each line once and carries charges larger than the cash | `PayoutTests`, `MoneyTests` |
| Every entity (except Platform) has a TenantId | `TenantOwnershipTests` |

---

## Decisions log

The decisions and their reasons are in [Project-Context.md §4](../Documentation/Project-Context.md#4-decisions-and-the-reasons-for-them).
New ones are added there and dated here.

| Date | Decision |
|---|---|
| 2026-10-04 | Pivot to a traditional courier; start from `3b48bc9`, stash the uncommitted Week 5 rewrite; keep multi-tenancy with one courier; reset both databases; public tracking only (owner) |
| 2026-10-04 | SQL Server reached at `10.50.0.1,1433` over the VPN instead of `ras-x2` (owner) |
| 2026-10-06 | SQL Server reached as `ras-x2,1433` again (owner) |
| 2026-10-06 | The coverage map stays seeded but the admin adds, edits and takes off hubs, zones and areas; what comes off is archived, never deleted, and a part of the map with work on it refuses to move (owner) |
| 2026-10-06 | The features missing against Steadfast's merchant panel become Phase 3, in the owner's order, moderators first; no prepaid wallet, no Pick-n-Drop, no Bangla switch (owner) |
| 2026-10-06 | A moderator is a merchant login plus a permission row on the account's main profile; merchant pages are default-deny against a page-to-permission list; the first password is handed over by the owner because there is no email sender |
| 2026-10-06 | Email replaces SMS as the real channel, for merchants only: a real SMTP sender (Gmail app password in `appsettings.Local.json`) carries the payout notification; recipients keep the fake SMS until there is a gateway (owner) |
| 2026-10-06 | The bKash / Nagad / bank payout gateways and the app's own SQL login are left as they are until the owner has the accounts and makes the call (owner) |
| 2026-10-07 | Problems a hub flags (in review, exceptional) are a flag beside the parcel's status, not new statuses; a flagged parcel is not handed to a rider; only a parcel the courier has can be flagged |
| 2026-10-07 | Returns go back on a return list a rider carries; the hand-over returns and charges each parcel, the merchant's confirmation is a signature, not a gate |
| 2026-10-07 | After pickup a merchant's cancel or cash change is a request the admins answer; this replaces the merchant's direct "Ask for it back" |
| 2026-10-07 | Several payout accounts per merchant account, the one in use copied onto the main profile; "Get paid now" pays every unpaid line at once with the run's own rules, no limit on how often |
| 2026-10-07 | The bell is worked out from existing rows when a page is drawn; "seen" is a cookie per login. The merchant ID is shown (owner's request) as the one exception to "never show a raw id" |
| 2026-10-07 | Phase 4: payouts the admin can steer (refused transfers with their reason, send again, cancel; hold one account; adjustments), then the admin's bell, settings, staff, merchant, stuck parcels, rider pages and a pass over every page (owner). Payouts keep not waiting for a rider's cash |
| 2026-10-08 | "Get paid now" becomes a request the courier's admin approves; it paid at once, which could pay out cash still with a rider, kept nothing back for parcels that may come back, and had no limit (owner) |
| 2026-10-08 | SSLCommerz (owner's sandbox) takes money in, first for merchants paying what they owe: an adjustment credit, believed only when the gateway confirms it to our server, once. Not a payout gateway (it cannot send money out) and no prepaid wallet (the payout run would pay a top-up straight back). Recipients paying COD online is the recommended next step |
| 2026-10-08 | Ready to go live for the portfolio: a clean database with one admin from settings, no demo data outside Development, the courier on one address, `deploy/` with HTTPS; the hosting and the address are the owner's senior's to choose; the dev database is left as it is (owner) |
| 2026-10-12 | Role-based staff (5.1): a job at one hub or at head office, preset permissions plus ticks, pages default-deny; a super admin manages everyone, a hub manager their own hub, nobody gives what they do not hold; existing hub logins became relief staff (owner)
| 2026-10-13 | Delivery speeds and kinds of contents are the admin's rows, priced per service area; parcels point at them and keep their extras; the API takes a fixed code made from the first name (owner) |
| 2026-10-13 | The van takes whatever is waiting when it reaches a merchant and visits each pickup point once a day: no pickup cut-off; a parcel booked after the day's visit goes with the next day's pickup (owner) |
| 2026-10-13 | Only a pickup van collects from merchants (bicycles and motorbikes deliver); a collected parcel stays on the van until the hub scans it in; every hand-over — pickup, delivery run, return list — is checked against what the rider already carries, at the hub's weighed weight (owner) |
| 2026-10-16 | A live start with no admin named makes the demo admin (`admin@onedrop.test`), so a fresh server can always be signed in to; a named admin never falls back to it. The demo password is public until changed (owner) |
| 2026-10-16 | Wrong passwords no longer lock a login; a stopped login is still refused and says it was stopped. Nothing now slows guessing at a sign-in password (owner) |

---

## Daily log

Newest first.

### 2026-10-16 (the live site's first admin)
- **Owner's report:** the owner's teammate put the site live from GitHub (at `delyvo.octopi-digital.com`) and nobody could
  sign in: the owner tried the demo login (`admin@onedrop.test` / `OneDrop#2026`), which only the Development demo seeder
  makes. Owner's request: "when the project is live it automatically generates the super admin". **Found at the push:**
  the live site is on IIS (the teammate's `Deploy-Iis.ps1`, settings on the app pool), not the Docker `deploy/`, so the
  `.env` advice given earlier in the session did not apply to it; the fallback below needs no setting at all.
- **Done:** outside Development, with `Setup:AdminEmail` empty, `Program.cs` makes the admin from
  `Setup:DefaultAdminEmail` / `Setup:DefaultAdminPassword` (in `appsettings.json`) through `AdminSetup`, with a warning
  in the log; an admin that is named (even with no password) never falls back to it. `deploy/docker-compose.yml` no
  longer requires `ADMIN_EMAIL`; `.env.example`, `Deployment.md` and Project-Context say so, with the risk: the
  password is public until it is changed at `/Account/Password`. A server that already names its own admin is
  untouched: to get the demo admin there, empty `ADMIN_EMAIL` and `ADMIN_PASSWORD` in its `.env`.
- **Tested:** build 0 warnings; `docker compose config` shows the three settings pass through (empty, named, and the
  `#` in a password kept); integration `LiveSetupTests` 4 of 4 (new: a Production start with nobody named makes the
  default admin and it signs in to `/Admin`, and a named admin with no password stops the start and the default is not
  made). The class's host test was already failing on `main` (the factory's empty `Tenancy:HomeCourierHost` clobbered
  the test's own value); it now sets the value through the app's configuration and passes. **Not done:** the other
  suites and a run on the live server (the change is not committed, so it is not there yet).
- **Then (live site still locked):** after the push the owner could still not sign in: `admin@onedrop.test` already
  existed on the live server with a password nobody had, and 5 wrong tries had locked it (the sign-in said "Too many
  attempts", which only an existing login gets), so the fallback, which never touches an existing admin, did nothing.
  The owner asked for it to work with nothing for the teammate to do. Added `Scripts/2026/012_DemoAdminPassword.sql`
  (a one-time DbUp reset of that one login to the demo password, unlocked; creates no login), which the teammate's
  usual `Deploy-Iis.ps1` runs in its database step. Tested: `LiveSetupTests` 5 of 5 (new: an admin with another
  password and a lockout is let in with the demo password after the real script, aimed at its email; the old password
  fails; a login that is not there is not made); `publish.ps1 -Database OneDrop-Test` ran 012 through DbUp (exit 0).
- **Then (owner): "Remove the too many attempt blocking."** The message was Identity's lockout (five wrong tries, five
  minutes). Sign-in now calls `PasswordSignInAsync` with `lockoutOnFailure: false` and the limit is gone from
  `AddAppIdentity`; a login an admin or owner stopped is still refused (a stop is a lock to the end of time, checked
  whatever the failure setting is) and says "This login has been stopped". Sign-in had no per-address limit, so
  nothing now slows guessing; only tracking, tracking payment and sign-up have one (30 a minute), left alone.
  Tested: build 0 warnings; domain 262, architecture 6; integration `SignInTests` (new: eight wrong passwords, never
  "Too many attempts", the right one signs in and the login's failed count is 0 with no lock; it failed on the old
  code at the fifth try), `LiveSetupTests` 5 of 5, `ModeratorsTests` 2 of 2 and `StaffTests` 1 of 1 (a stopped login
  is still refused). Not run: the full integration suite.
- **Caught by that test:** removing the rule does not free the logins it had already locked. Sign-in refuses a lock
  whatever the failure setting is (`PreSignInCheck`), so the live admin, and any merchant who had mistyped five times,
  would have stayed out for good. `Scripts/2026/013_ClearOldLockouts.sql` clears the lock and the count on every login
  that was not stopped (`Archived = 0`); a stopped one keeps its lock, which is what stopping is. Tested:
  `SignInTests` 2 of 2 (new: a login locked as the old rule left it is refused, the script lets it in, a stopped one
  stays out) and `publish.ps1 -Database OneDrop-Test` ran 013 through DbUp (exit 0).

### 2026-10-16 (places of Bangladesh and the step-by-step area picker)
- **Owner's request:** the delivery area was one long dropdown; make it Division, then Zone (Dhaka >), then Area (Dhaka > Mirpur >), with every place in Bangladesh.
- **Done:** `Zone.Division`, `Divisions`, `010_BangladeshPlaces.sql` (53 district zones and about 485 upazila areas, launch zones filed by division), a Division field on the Coverage zone forms, and the picker: the same `_AreaSelect` partial (so booking, sign-up, new business, pickup points, rates and the admin's new merchant all use it) with a search box. Without a script it stays an ordinary grouped select. Also today: the hub menu marks hubs with work with a red badge, "Coming in" counts parcels on the van, the assign page shows the chosen rider's load including ticked parcels, and the sign-in page no longer lists demo accounts.
- **Tested:** build 0 warnings; domain 262, architecture 6; both databases published; integration `CoverageAdminTests`, `SchemaMatchesModelTests`, `ParcelApiTests`, `RowLevelSecurityTests` (18) and `PortalPagesTests`, `IsolationTests`, `DeliveryTypesTests`, `DailyPickupTests` pass (the hub-menu test was updated for the new menu). The picker was clicked through on the sign-up page.
- **Worth knowing:** the district hubs are a first guess (for example Faridpur on the Motijheel hub); the admin changes them on Coverage. Upazila names come from memory of the official list: have the owner skim them.

### 2026-10-16 (pickup pins on the rider's route, a plainer hub board, 4.17)
- **Owner's request:** the map is for the rider's route (every pickup and delivery stop from where they are), so the merchant's pickup point needs a pin too; make the Hub operations page easier for the operator.
- **Found already built, uncommitted:** `PickupPoint.Latitude/Longitude` and `/Merchant/PickupPoint/{id}`, Navigate and Call per pickup stop, the route card (`MapLinks.Route`, pickups first then doors, ten stops per link), and the hub board as "What needs you now" plus three phases. Dev database was already current; `OneDrop-Test` published (`Scripts/2026/009` ran).
- **Done now:** the hub's `/Hub/Pickups` lists each pickup with a "See on the map" link; `PortalPagesTests` pickup test now posts `shown` like the rider's form (it was stale).
- **Tested:** build 0 warnings; domain 261, architecture 6; `DailyPickupTests` 9 of 9, `PortalPagesTests` 13 of 13. **Not done:** a look at the hub board and the pin page in a browser at desktop and phone width; the full integration run.

### 2026-10-13 (later: vehicle limits that hold, and pickups only by van, 4.15)
- **Owner's request:** from two screenshots (`/Admin/Vehicles` and `/Hub/Pickups`), "we selected a specific limits for
  each type vehicles. But here as you can see its not actually working. As the rider Rizvi he is using cycle and his
  limit is 15 kg but here he can be assigned to take more then that … Also the Cycle and Bike delivery rider will only
  take the parcel to the Customer but they wont pick up parcel form the Marchant. only the pickup truck will take the
  parcel. So every hub will have a dedicated pickup truck rider who will go out at 3:30."
- **The two holes found.** A pickup was sized against an *empty* vehicle (`Fleet.For` picked the smallest vehicle that
  carries it, then `Collect` let the rider through if theirs was that vehicle **or bigger by its own measure**), so
  nothing asked what the rider was already carrying: Rizvi's bicycle took a 25.5 kg pickup on top of whatever he had.
  And once collected, a parcel was nowhere at all — `PickUp` left `CurrentHubId`, `TransferToHubId` and `RiderId` all
  null — so even a correct check could not have seen what was on the van. A return list was never checked against any
  limit, and every check used the merchant's declared weight rather than the hub's scale.
- **Done (4.15):** `Vehicles.CollectsPickups` (the van alone) and `Load.Plus`; `Fleet.Collect(vehicle, carries, load)`
  refuses a non-van outright ("does not collect from merchants") and otherwise weighs the pickup against what the van
  holds (`pickup.full`), with the one exception that a pickup bigger than the whole van goes to an empty van, which
  makes more than one trip (`Fleet.Trips`). `Fleet.For` is gone: the question is no longer which vehicle but whether
  this van has room. `Parcel.PickUp(today, riderId)` keeps the parcel on the van until the hub scans it in (`ReceiveAt`
  already clears it; the location check already allowed it, so no schema change). New `RiderLoads` adds up a rider's
  parcels (held, out for delivery, on a return list, on the van) at `BilledWeightGrams` plus the parcels at the points
  they are on their way to collect, and `AssignParcelsHandler`, `PickupsHandler`, `ReturnListsHandler` and the rider's
  own pickup all read it. The round (`AssignAllAsync`) now takes pickups oldest first until the van is full and returns
  a `PickupRound` saying how many stayed behind and why. A rider's vehicle cannot change while they carry anything.
- **UI:** `/Hub/Pickups` offers only vans, shows a load meter per van, says when a pickup needs more than one trip, and
  warns a hub with no van; `/Hub/Assign` greys out and explains a parcel heavier than any vehicle of the hub takes, and
  the room hint now catches a too-heavy parcel before the post; `/Admin/Vehicles` and the guide explain the rules;
  `/Admin/Riders` names the staffed hubs with no pickup van; the admin dashboard's setup checklist asks for one. The
  demo seeder gained a van at Mirpur, Gulshan, Chattogram, Uttara and the rival hub.
- **Tested:** build 0 warnings; domain **260** (`DeliveryTests` rewritten for the new signatures: only a van collects, a
  pickup refused when the van is too full with the exact message, a pickup bigger than the van onto an empty van and
  refused onto a loaded one, a parcel heavier than the vehicle); architecture 6. Both databases published
  (`Scripts/2026/009`, data only). Integration: `DailyPickupTests` and `DeliveryFlowTests` **9 of 9** (new:
  `Only_a_pickup_van_is_sent_to_a_merchant_and_carries_what_it_collects_until_the_hub_scans_it_in` — a bicycle and a
  motorbike are refused and not shown in the picker, the van collects, the parcel carries its id until the Receive
  scan).
- **Worth knowing:** `Fleet.Collect`'s old "or theirs is the biggest" escape is what made the limits look decorative;
  any "can this vehicle take it" question has to start from what the vehicle already has, which means the load must be
  recorded somewhere the next question can read it.

### 2026-10-13 (delivery types the admin keeps, 4.14)
- **Owner's request:** "Make what is inside and Delivery type dynamic so that admin can change or add new one", then
  "the Delivery types page doesn't looks so user friendly … make it's UI more user friendly and easy to understand".
- **Done (4.14):** `Domain/Pricing/DeliveryOptions.cs` (`DeliveryOptionKind`, `DeliveryOption` with its code, name,
  description, handling, icon, goes first, default, order and `Charges`; `DeliveryOptionCharge`); `DeliveryRate.Price`
  takes the two options and `ParcelCharges` carries `SpeedId` and `ProductTypeId`; `Parcel.Speed` and `ProductType`
  are navigations. Tables `Pricing.DeliveryOption` and `Pricing.DeliveryOptionCharge` (in the policy), `Parcel.SpeedId`
  and `ProductTypeId`, the six extras columns gone from `DeliveryRate`; `Scripts/Pre/004` migrates, `Scripts/2026/008`
  seeds a new database, `005` no longer sets the dropped columns. `ParcelBooking` resolves an option by code or name
  (blank: the default); booking, edit, CSV, the API (`speed` and `productType` are codes; the charge endpoint lists
  `speeds` and `contents`) and the quote follow. `DeliveryTags` carries the names, icons, handling and "goes first" to
  lists, labels, hub screens and the rider's app; "goes first" orders the hub board, the assign page and the rider's
  run. `DeliveryOptionsHandler` and `/Admin/DeliveryTypes` (setup permission, menu, guide): a worked example of the
  charge, a table per kind, and an edit window in three steps with a live preview and a tick per service area. The
  booking form, the merchant's rates page and the front page read the options; the Rates page points to the new page.
- **Tested:** build 0 warnings; domain **259** (17 new in `DeliveryTypeTests`), architecture 6; both databases
  published (Pre/004 and 008 ran; on `OneDrop-Test` all 2,061 parcels kept their options and both couriers' prices
  carried across, the rival's test prices included). Integration: `DeliveryTypesTests` (4, one new: the rival's admin
  adds a speed and contents, an unticked area stays off, a merchant books with the new codes at 70 + 80 + 25, the label
  shows them, OneDrop sells none of them, the default cannot be taken off, a speed taken off is refused and the parcel
  keeps it), `SchemaMatchesModelTests`, `RowLevelSecurityTests`, `ParcelApiTests`, `DailyPickupTests` and
  `IsolationTests` (new sweep line): 25 of 26 in the first run, the one failure the Development sign-in page timing
  out on thousands of test logins (a known trap, not this change); `DeliveryTypesTests` 4 of 4 again after the
  redesign. Live on dev at 1440 px and 390 px: the overview, the Express and Fresh food windows, no sideways scroll.
- **What the screenshots caught, fixed:** the first page (a form card per option) was a wall of fields; "+৳" wrapped
  and placeholders were cut; a hidden "Actions" header label escaped the scrolling table and widened the phone page;
  the price sum's figures took the caption style.

### 2026-10-12 (last: role-based staff, 5.1)
- **Owner's request:** "The Hub staff we have here doesn't have specific role. All of the staff is same role now. I
  want to have role based staff authorization." The owner took the four recommendations: a preset plus ticks; the
  super admin and a hub manager (own hub, never more than they hold) make logins; one hub or head office; head office
  jobs in the same round.
- **Done (5.1):** `Domain/Staff` (`StaffPermissions` bit field, `StaffJob` presets, `StaffMember`: hub jobs need a hub,
  head office jobs none, head office work never at a hub); `Identity.StaffMember` in the policy, `007_StaffJobs.sql`
  (old hub logins become relief staff with every hub desk); `StaffAccess` (rights once per request; `TenantAdmin` is
  super admin, a staff login with no row may do nothing); `StaffPermissionFilter` and `StaffPages.cs` (default-deny,
  page and "page/handler") on `/Hub` and `/Admin`, which now admit any staff login; the hub lock in
  `HubOverviewHandler` (and `RememberHub`, `_HubSwitch`: no hub menu for a hub's staff); the parcel page's buttons
  refuse a parcel at another hub; the scanner shows only a person's modes; menus, favourites, the bell, work marks,
  the dashboard and the hub board's steps by permission; a home per job; `/Admin/Staff` rewritten (job, hub, ticks,
  who manages whom); a hub with staff cannot close; demo staff `mirpur@`, `receiving@`, `accounts@onedrop.test`.
- **Tested:** build 0 warnings; domain **242** (4 new `StaffMemberTests`), architecture 6; both databases published
  (007 ran on each). Integration **42 of 42, none skipped** (26 min): new `StaffRolesTests` (receiving clerk locked
  to Mirpur and its desk, another hub's parcel refused; hub manager adds only to Mirpur and only what they hold;
  accounts at head office, an operations manager with finance), updated `StaffTests`, plus `PortalPagesTests`,
  `BellTests`, `IsolationTests`, `AccountMenuTests`, `CoverageAdminTests`, `FailedMessagesTests`,
  `CourierSettingsTests`, `DeliveryFlowTests`, `SchemaMatchesModelTests`, `RowLevelSecurityTests`.
- **Owner's look:** changing a person's job kept the old job's boxes ticked (and, on the edit form, posted them as
  extra ticks); the script now clears the old job's boxes and keeps only the hand-ticked ones.

### 2026-10-12 (staff jobs proposed)
- **Owner's question:** is there an employee role in the admin panel; in real life each hub has people for each job
  (receiving, sorting onto racks, scanning out to other hubs, returns, accounts). There is not: `TenantAdmin` does
  everything and `HubStaff` can do every hub job at every hub. Proposed Phase 5 (staff with a hub and permissions,
  job presets, racks, bags between hubs, hub cash deposits); written down, nothing built. Four questions wait on the
  owner.

### 2026-10-12 (later: one board for every hub)
- **Owner's request:** in the admin panel, "Hub operations" opened a chooser of hub cards with their waiting work,
  "but if we think of real life all the hub's will have waiting work". Show the normal hub board over every hub
  instead, with "All hubs" in the hub menu and a hub's own board when one is chosen.
- **Done:** `HubBoardHandler.GetAllAsync` builds the same board over every hub (one query, not one per hub); each
  parcel carries the hub it is at (`BoardParcel.HubCode`), and the send-on and hand-back groups name theirs.
  `RememberHub.AllHubs` (`?hub=*`, remembered like a hub code) and `WantsAll`: an admin who has not chosen lands on
  every hub; hub staff still choose first. Only the board offers it (`HubPage.OffersAllHubs`); scan, assign, closing,
  pickups and returns still send to the chooser. On the board of every hub the steps and buttons open the chooser for
  their page (the chooser now carries the scan mode, and opens the one hub with parcels coming in straight away),
  the rider list has a Hub column, and the side cards list each hub's parcels to send on and returns to hand back,
  linking to that hub. `_HubSwitch` takes the page: "All hubs · n waiting" first on the board, the chooser link on the
  other pages.
- **Tested:** build 0 warnings; domain 229, architecture 6; integration `PortalPagesTests` **13 of 13**, none skipped (new:
  `Admins_open_one_board_for_every_hub_and_pick_a_hub_from_its_menu`: the admin lands on every hub with the steps
  opening the chooser, picks MIR, the remembered `*` brings every hub back, hub staff still choose first and can ask
  for every hub, scan with `*` goes to the chooser, the rival courier's board holds only its own hub). Live on dev
  with curl as the admin: `/Hub` all hubs (2 waiting), `?hub=MIR` Mirpur's board and the cookie, `?hub=*` back,
  the `%2A` cookie read back, `/Hub/Scan` with it sent to the chooser. **Not done:** a screenshot (the DevTools
  browser was held by another session).

### 2026-10-12 (delivery types, the door on a map, one pickup a day)
- **Owner's request:** delivery types in booking (fast, fragile, frozen and more, priced differently: fragile above
  regular, frozen above fragile); Google Maps for the recipient so the rider finds the door; in the merchant menu drop
  "Add business" and rename "All businesses" to "Manage businesses"; one pickup a day, the truck leaving the hub at
  3:30 pm with the parcels booked before 3:00 pm, later ones the next day, and no manual pickup request.
  Asked and answered: Google Maps everywhere (the owner adds a key), speed and contents as two choices, a pickup
  every day.
- **Done (4.13):** `DeliverySpeed` and `ProductType`, the extras on `DeliveryRate` (`Price` refuses what a service area
  does not offer), snapshot on the parcel (`SpeedCharge`, `ProductCharge`, in `DeliveryCharge`; a reweigh keeps them).
  `GeoPoint` reads coordinates and every Google Maps link form; `GoogleMapLinks` opens short links (Google's hosts only).
  Booking form with option cards priced live, the map pin (Google map, search, click, drag, find the address; or a
  pasted link without a key), itemised charges and the pickup day; CSV columns `speed`, `product_type`, `location`;
  API fields; labels, hub lists, assign page, rider app (Navigate, embedded map with a key) show tags and handling, and
  express and fresh or frozen food go first. Admin rates page edits the extras; merchant rates page and front page show
  them. Pickups: `Tenant.PickupCutoff` and `PickupTime`, `PickupSchedule` (booking lists the point; the `pickups` job
  carries, lists and calls off), riders collect only parcels booked before the cut-off, hub "Today's pickups" with one
  rider for the whole round, the merchant's read-only Pickups page; the request form, cancel and the "ask for a pickup"
  nudges are gone. A pickup point now moves zone once no parcel waits and no rider is on the way (an empty scheduled
  pickup is called off). Both databases published (`Scripts/Pre/003`, `Scripts/2026/005`).
- **Tested:** build 0 warnings (Web compiled to a side folder while an elevated `Web.exe` held `src/Web/bin`); domain
  **229**, architecture 6; integration `DailyPickupTests` 4, `DeliveryTypesTests` 4, `DeliveryFlowTests`, `BellTests`,
  `SchemaMatchesModelTests`, `RowLevelSecurityTests`: all pass (one test fix: Razor writes `=` in links as an entity).
  A first wider run (isolation sweep, portal pages, API, merchant panel, guards, businesses) lost the `ras-x2`
  connection midway; it found the old pickup-request test and the pickup-point guard, both fixed.
- **Worth knowing:** a check constraint `x BETWEEN … AND y IS NOT NULL` is false, not unknown, when `x` is NULL; write
  `x IS NULL OR (…)`. The pickup catch-up job in `OneDrop-Test` lists every old test merchant's pending parcels once.

### 2026-10-08 (last: ready to go live)
- **Owner's request:** "I will show it in my portfolio so make it ready to be live. Remove if there is any demo data."
  Asked and answered: the hosting and the address are the owner's senior's to choose; a clean start with only the
  owner's admin; the dev database `OneDrop` (the owner's own test sign-ups, 13 parcels) is left as it is.
- **Found:** the only admin logins anywhere were the demo ones, with their password in the README, and a database
  deployed from nothing had nobody who could sign in (only the Development demo seeder made users); behind a proxy the
  app saw http and the proxy's address; the courier needed `onedrop.<domain>`; a mistyped address showed raw JSON; the
  README's first line was a broken fragment on GitHub; the local `docker-compose.yml` promised demo logins it no
  longer made (`Seed:DemoData` has been off since 2026-10-04).
- **Done (4.12):** `AdminSetup` makes the courier's admin from `Setup:AdminEmail` / `Setup:AdminName` /
  `Setup:AdminPassword` when missing and never changes one that exists; a bad password stops the start with the reason.
  `Tenancy:HomeCourierHost` serves the home courier on one address (and the bare domain sends visitors there).
  `deploy/docker-compose.yml` (SQL Server 2025 Express, the database deployed on every start, the app as Production
  with `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, Caddy for HTTPS), `deploy/Caddyfile`, `deploy/.env.example`, LF by
  `.gitattributes`. `Documentation/Deployment.md` for the senior (server, address or sslip.io, settings, start, first
  sign-in, updates, backups, other hosts, troubleshooting). Pages get a "We could not find that page" page for an empty
  error answer; the API keeps problem details. README: title, live-site links, the demo-data wording; the local compose
  turns the demo on explicitly.
- **Tested:** build 0 warnings; domain 203, architecture 6; integration `LiveSetupTests` 3 of 3 (admin made once and
  never changed, refused at another courier, weak password and unknown courier stop the start; one-address host;
  error pages), `PortalPagesTests` 12 of 12, `IsolationTests` passed with the new error page. **The production stack
  run for real on Docker Desktop** with `SITE_ADDRESS=localhost`: database deployed from nothing (16 hubs, 87 areas, 0
  merchants, 0 parcels, one login), "Setup: created the admin", http redirected to https, the admin signed in (secure
  cookie) and saw the setup checklist, no demo buttons, `/Dev` 404, a restart without the password left the admin as
  it was, the backup command from the guide produced a file, SQL Server reported Express Edition. Torn down afterwards.
- **Worth knowing:** `LiveSetupTests`' host test failed once beside the isolation sweep because the Development
  sign-in page took over 100 s (now a trap in Project-Context); it requests `/Coverage` instead. The first database
  deploy in Docker Desktop took about 6 minutes.

### 2026-10-08 (later still: an admin panel that says where to go next)
- **Owner's request:** "make the whole admin panel more userfriendly, so that admin easily understand where to go after
  where."
- **Dashboard, read top to bottom.** A setup checklist while the courier is not set up (map, rates, riders, an active
  merchant, in that order, each with its button); "Waiting for you", the admin bell's lines as a to-do list, each
  opening the page where it is settled, or one line saying nothing waits; "The parcels' day, across every hub", six
  numbered steps like the hub board (pickups, coming in, waiting for a rider, out with riders with runs to close and
  the cash, going back, owed to merchants), each opening the hub chooser on the step's page; then today's numbers,
  the week, parcels in progress, hubs and top merchants. `AdminDashboard` gained `PickupsWithoutRider`, `OpenRuns` and
  `Setup` (`CourierSetup`).
- **`/Admin/Guide`, "How it works".** Set up once in order (coverage, rates, vehicles, riders, merchants); a parcel's day
  in eight steps with who does each and a link to its page, and the way back for returns; what needs the admin and
  where; where to look back. In the menu under the dashboard, on the dashboard and in a sidebar box for admins.
- **Menu by when it is used:** Dashboard and How it works; Every day (hub operations, parcels, requests, failed
  messages); Money; People; Setup (coverage, rates, vehicles, in setup order). Hub operations stays lit on every hub
  page, the chooser and run sheets included.
- **Toasts offer the next step:** a new hub, "Draw a zone on it"; a new zone, "Add its areas"; a new rider, "Hand them
  parcels at {hub}"; an approved merchant, "Next sign-up (n waiting)" while others wait.
- **Caught on the screenshots:** `.section-head` is the public site's (42 px headings), the trap already listed; the
  admin's headings are `.part-head`.

### 2026-10-08 (later: tabs that do not reload the page, and work marks in the menu)
- **Owner's requests:** "In payout box when i click any one these [All, Paid, Processing, Cancelled] the whole page
  loads", then "In side menu add a red ! circle for tabs … where there is work to do … for admin, marchent, rider panel".
- **Tabs in place.** `site.js` gained a shared behaviour: a part marked `data-swap="name"` whose link points to the same
  page with another query fetches the page and swaps only that part, and pushes the address, so back, reload and a
  shared link still work; anything unexpected (an error, a redirect to sign-in) falls back to following the link. The
  part fades only when the answer is slow. Used by the payouts card on `/Admin/Payouts`; other filters can opt in.
- **Work marks.** `MenuWorkHandler` answers, with a few `EXISTS` reads per page, where open work waits: for the admin
  sign-ups, requests, flagged parcels (Parcels), work at the hubs, failed messages and stuck payouts (refused, held
  online payments, owed with no payout account); for the merchant parcels booked with no pickup asked for, return
  lists to confirm, and no payout account (Settings); for the rider deliveries or return lists to hand over, and
  pickups to collect. The layout puts a red "!" on those menu items and favourites, and on the phone's menu button.
  What merely happened stays in the bell.
- **Tested:** build 0 warnings; architecture 6; `BellTests` 3 of 3 on `OneDrop-Test` (new: the merchant's pickups
  marked once it books and cleared once the parcel is at the hub, another merchant unmarked, the rider's deliveries
  marked once assigned, the admin's merchants marked for a sign-up). Live on dev: the payouts page carries the swap
  part and the served script has it. **Not done:** a click-through in a browser (the DevTools browser was in use), so
  the swap itself has not been seen working; the hub staff's own menu has no marks yet.

### 2026-10-08 (SSLCommerz: merchants pay what they owe online)
- **Owner's request:** "i opend a ssl for the demo mode" (an SSLCommerz sandbox store). Asked what it should be for, the
  owner picked every option and said: "i dont know. Im confused about this. im doing this project just to show in py
  portfolio. But i want the logic to be perfect." Recommended and built: merchants pay what they owe (4.9); recipients
  paying COD online (4.10) next, with refunds; no prepaid wallet (the hourly payout run would pay a top-up straight
  back); SSLCommerz cannot be the payout gateway (it only takes money in).
- **4.9 done.** `Domain/Payments/OnlinePayment.cs` (started, paid, failed, held for review, refunded; `Confirm` credits
  an `Adjustment` line once, even after a failure; risky or another amount is held; `MethodName` reads "BKASH-BKash"
  as "bKash"). `IPaymentGateway` with `SslcommerzGateway` (session API, validation API, transaction query),
  `FakePaymentGateway` and `/Dev/Pay` (Development without a store), `NoPaymentGateway` elsewhere.
  `OnlinePaymentsHandler` (start from the server's own balance; return, notice and the `online-payments` job all ask
  the gateway first; the admin credits or records a refund) and `PaymentReturnController` (`/pay/{transaction}/…`,
  `/pay/notice`, open to anyone, nothing posted believed). Pages: "Pay ৳X online" and a return banner on
  `/Merchant/Payments`, "Paid online" on statements, held payments and the latest on `/Admin/Payouts`, a line in the
  admin's bell. Table `Payments.OnlinePayment`, sequence `Payments.OnlinePaymentNumber`, in the security policy; both
  databases published. The store id and password are in the git-ignored `src/Web/appsettings.Local.json`.
- **Tested:** build 0 warnings; domain **200** (13 new), architecture 6; integration: the full suite **94 of 94, none
  skipped** (three new `OnlinePaymentsTests`: credited once, a forged return credits nothing, the check job credits a
  payer who never came back and closes an unfinished one, risky held and credited by the admin, cancelled credits
  nothing, another courier reaches none of it; sweep lines for `/Dev/Pay` and both `/pay` routes). After the last
  changes (sorting, admin layout, method names, and the tests forced onto the test gateway because the real store
  is now in `appsettings.Local.json`) `OnlinePaymentsTests` again: **3 of 3**.
- **Live:** on `OneDrop-Test` with the test gateway, then with the owner's **real SSLCommerz sandbox**: Chattogram
  Crafts charged ৳150, paid by bKash on SSLCommerz's page, credited (the gateway said ৳146.25 reaches the courier after
  its fee); charged ৳120, paid by Nagad with "Success with risk", held, shown to the admin with the gateway's reason
  ("Not Safe"), credited. Merchant page at 1440 px and 390 px, no sideways scroll.
- **What the live check caught, fixed:** the gateway's raw label ("BKASH-BKash") on statements, the pay dialog in the
  warning colour, and the admin's refund box running off the table.
- **Then (owner):** "dont show this page at localhost:5080. i want it to start with http://onedrop.localhost:5080/".
  The bare domain now sends visitors to the courier named by `Tenancy:HomeCourier` (`onedrop`; the list of couriers
  shows only when none is set); platform admins still sign in at `localhost:5080/Account/Login` and land on the list.
  The launch profile opens `http://onedrop.localhost:5080/`. New `PortalPagesTests` case; the class passed 12 of 12.
- **Then (owner):** "whats is the logic here. if there is a get paid now then everybody will take out the money".
  Explained the three holes (cash still with riders, nothing kept for parcels that may come back, no limit) and offered
  rules-based instant payouts, a request the admin approves, or removing it; the owner chose the request (4.11).
  `Domain/Payments/PayoutRequest.cs` (open, paid, refused, withdrawn), table `Payments.PayoutRequest` (one open per
  business, in the policy; both databases published), `PayoutRequestsHandler` (ask, withdraw, the admin's review with
  balance, today, cash with riders and charges if returned, pay through `PayoutsJob.PayNowAsync`, refuse). The
  merchant's "Get paid now" is gone; "Ask to be paid now" and "Take back the request" replace it, a refusal shows on
  Payments and in the merchant's bell; the admin sees "Asking to be paid now" on `/Admin/Payouts` and a bell line.
  Tested: domain **203** (three new `PayoutRequestTests`); integration `PayoutRequestsTests` (2, new), `BellTests`,
  `PayoutTests`, `PayoutAccountsTests` (the old pay-now test moved into the new class), `SchemaMatchesModelTests`,
  `RowLevelSecurityTests`: **18 of 19**, the one failure a clash with `PayoutTests` (below), fixed and rerun.
- **Then (owner):** "it doesn't show the red reminder on the tab that a work is needs to done". The menu's work marker
  (`MenuWorkHandler`) did not know about payout requests; it now marks Merchant payouts while one is open. On a
  favourite the "!" sits on the chip's corner like the bell's count, because inline it widened the chip and pushed it
  out of a full favourites bar. Also fixed from the live look: "cash still with riders" on a request counted cash of
  parcels already paid out; it now counts only the cash in the balance being asked for. `PayoutRequestsTests` moved
  to the rival courier: `PayoutTests` runs OneDrop's payouts as of tomorrow and paid a test merchant's lines out from
  under it. Rerun there: **2 of 2**, including the menu marker.
- **Worth knowing:** SSLCommerz's merchant panel is `sandbox-gw.sslcommerz.com` but its API is still
  `sandbox.sslcommerz.com`. Its notice cannot reach localhost, which is why the check job exists. The owner asked
  (again) never to run the full integration suite after a change, and to keep it in the background when it is needed.

### 2026-10-07 (later: Phase 4 begins, payouts and the admin's bell)
- **Owner's request:** after "do merchant payouts work?" and a list of what the admin panel lacks, "Lets fix these". Became Phase 4.
- **4.1 done.** `Payout` keeps `FailedAttempts`, `LastError`, `LastTriedOn`, `AdjustmentsTotal` and can be `Cancelled` (its lines freed); `LedgerEntry` kind `Adjustment` with a `Note` and no parcel; `Merchant.PayoutHold` per account. `PayoutsJob.SendAgainAsync`, `CancelAsync`, `PayNowAsync` returning whether the money left; `IJobSchedule` reads Hangfire for the last and next run. `/Admin/Payouts`: how payouts work, the run's times (and a warning when late), refused transfers with send again, owed and owing lists (first 15, with totals). `/Admin/Payout/{n}`: send again, cancel. `/Admin/Merchant/{id}`: hold, release, pay now, adjustments. `/Dev/Payouts` can make the fake refuse an account. Both databases published.
- **4.2 done.** `AdminBellHandler`: sign-ups, requests, flags, failed messages, refused payouts, owed with no account, short riders and return list notes of the week; drawn in the layout for admins.
- **Tested:** build 0 warnings; domain **187** (five new), architecture 6, integration **91 of 91, none skipped** (four new: refused and sent again, cancelled and paid to a corrected account, held and adjusted, the admin's bell). Live on `OneDrop-Test` (port 5090) at desktop and 390 px; no console errors, no sideways scroll.
- **What the live check caught, fixed:** a credit shown as a charge on the invoice (adjustments now kept apart on the payout), a cancelled invoice reading "Paid", pay now reporting success on a refused transfer, uncapped owed/owing lists, a cramped refused table. Also the status check not applied at first (caught by a test).
- **Worth knowing:** a full integration run takes 20 to 28 minutes over `ras-x2`. Cancel after a refusal can pay twice if the gateway did send the money; the dialog says to check first.

### 2026-10-07 (Phase 3: the Steadfast gaps, one by one)
- **Owner's request:** "Check how much work we have left" (seven Phase 3 tasks, plus the owner's review and the two
  items waiting on accounts), then "Ok do all of them one by one".
- **Baseline first.** Build 0 warnings; domain 161, architecture 6; integration **72 of 73**: the isolation sweep
  failed on `/Dev/Emails`, a page the last session added without its sweep line (the 2026-10-06 entry promised a full
  run it never recorded). Added to the sweep and to the "not outside Development" check.
- **3.2 More parcel tabs, done.** `ParcelTab` gained `PartlyDelivered` (Delivered is now full deliveries only; the
  dashboards count partly delivered apart, and the delivery rate still counts both), `InReview` and `Exceptional`.
  New `Domain/Parcels/ParcelIssue.cs`; `Parcel.Flag` (only while the courier has the parcel, a note of at most 200,
  keeps the time it was first flagged) and `Parcel.ClearFlag` write a history line each and raise no event, so no
  webhook; `Parcel.AssignTo` refuses a flagged parcel. Schema: `Issue`, `IssueNote`, `IssueRaisedOn`,
  `chk_Parcel_Issue` and the filtered `IX_Parcel_Tenant_Issue` (both databases published). Hub parcel page: "Flag a
  problem" (kind, what is wrong) and "Clear the flag" (what was done); the assign page shows a flagged parcel with
  no box to tick, the hub board shows the flag; the admin dashboard counts both kinds; the merchant gets the tabs, the
  flag in its list, an alert on the parcel and a banner on its dashboard; `GET /api/v1/parcels/{code}` returns
  `issue` and `issueNote`.
- **Tested:** domain **165** (four new: flag and clear, flagging again, who can be flagged, a flag raised while out
  with a rider); integration **76 of 76 over `ras-x2`, none skipped**, with three new `ParcelTabsTests` (a hub flag in
  the merchant's tab only, refused at the assign page, cleared and handed out; an admin's exceptional flag; partly
  delivered apart from delivered).
- **Live on `OneDrop-Test`** (port 5090: another app of the owner's, Grabity.Api, holds 5080) with headless Chrome at
  1440 px and 390 px: flagged OD10000519 at Mirpur as the admin, saw it refused a box on the assign page, in the
  "In review" tab, on the admin dashboard, and as Fashion House in the tab, the parcel and the dashboard banner; then
  cleared it ("Review finished: Live check finished"). No console errors, no sideways scroll.
- **What the screenshots caught, all fixed:** eleven tabs need 1577 px and the bar had 1118 px with its scrollbar
  hidden, so the last tabs (the open "In review" among them) were cut off on a desk; it was already a little over with
  eight. Tabs now wrap onto a second row on a desk and stay one swipeable row on a phone, where `site.js` scrolls the
  open tab into view. The red count first used the class `alert`, which is the page alert component (padding, border,
  margin), and blew the tab up; renamed `count-review` / `count-exception`.
- **Worth knowing:** `sed -i` from Git Bash rewrites a CRLF file as LF (it happened to Project-Context.md, put back);
  and line 1 of Project-Context.md held a stray copy of the moderators decision row from the last session's perl
  mishap, above the title. Removed.
- **3.3 Stats page, done.** New `Domain/Common/Period.cs` (the named periods, and a custom one checked: both days, in
  order, not after today, at most 366 days), `ParcelStatsHandler` (one `GROUP BY` over the parcels booked in the
  period, merchant-filtered, every status listed even at zero) and `/Merchant/Stats` (moderators with the dashboard
  permission; in the menu under the dashboard and among the dashboard's tools). Rates: delivery and return out of the
  parcels that reached an end with the courier, cancel out of everything booked; "—" while there is nothing to divide.
  Each status row links to the parcel list for its tab and those days.
- **Tested:** domain **171** (six new `PeriodTests`); integration **78 of 78, none skipped**, with two new
  `StatsTests` (five parcels in five states plus one booked forty days ago: counts, rates, cash, the custom period,
  a period backwards; another merchant counts none of them) and the sweep's `/Merchant/Stats` line. Live as Fashion
  House at 1440 px and 390 px: last 30 days, last month (the empty state), a custom range and a backwards one; no
  console errors, no sideways scroll.
- **What the screenshots caught, fixed:** the number cards were written flat (icon, label, value side by side), which
  the card style lays out wrongly; they need the label, value and note wrapped in one `div`, as the dashboard does. The
  admin's Reports page from the last session had the same flat cards and showed "Delivered 255 109 returned" in a
  jumble; fixed the same way. The custom-period date boxes stretched to full width; now 170 px each.
- **3.4 Returns the merchant signs for, done.** `Domain/Delivery/ReturnList.cs` (`ReturnList`, `ReturnListParcel`;
  out, handed over, confirmed, not handed over) and `Parcel.CanGoBackFrom` / `SendBack` / `HandBackAtDoor` /
  `MissHandBack`; tables `Delivery.ReturnList`, `Delivery.ReturnListParcel` and the sequence
  `Delivery.ReturnListNumber`, in the security policy. `ReturnListsHandler` (hub and rider; the hand-over writes the
  ledger lines in the same save) and `MerchantReturnsHandler`. Pages `/Hub/Returns` (also the hub board's "Hand back"
  step and the hub chooser), `/Rider/Return/{number}` with a "Returns to hand back" section on the rider's home,
  `/Merchant/Returns` and `/Merchant/Return/{number}`; a dashboard banner while a list waits to be confirmed.
- **3.5 Requests, done.** `Domain/Parcels/ParcelRequest.cs` and `Parcel.ChangeCod`; table `Parcels.ParcelRequest` (one
  open per parcel). `ParcelRequestsHandler`; the merchant parcel page's "Ask the courier" (change the cash, cancel)
  replaces "Ask for it back"; `/Merchant/Requests`, `/Admin/Requests` (in the admin menu, a dashboard banner), and the
  open request shown on the hub's parcel page.
- **3.6 Payout accounts and pay now, done.** `MerchantPayoutAccount` with the account check shared with
  `Merchant.SetPayoutAccount` (`PayoutAccounts.Read`); table `Merchants.MerchantPayoutAccount` and
  `Scripts/2026/004_PayoutAccounts.sql`, which kept each account's existing one (1,823 in `OneDrop-Test`).
  `PayoutAccountsHandler` behind Settings (add, use, remove; the in-use one cannot be removed); the admin's payout
  form keeps its account in the list too. `PayoutsJob.PayNowAsync` and "Get paid now" on Payments, which now splits
  the balance into ready, today and still to collect, and shows a mobile account in its local form.
- **3.7 The bell, done.** `MerchantBellHandler` works the last week's notices out of parcels, attempts, flags, return
  lists, requests and payouts; the layout draws it for merchants, `site.js` keeps "seen" in a cookie per login.
- **3.8 Small things, done.** `ParcelListHandler.ExportAsync` and `/Merchant/Export` (the parcel list's Export button
  now uses it, so it is no longer cut at 100 rows); `ParcelStatsHandler.ByDayAsync` and `/Merchant/ByDate`, sharing
  the stats page's period picker (`_PeriodPicker`); the merchant ID with a copy button in the account menu and on
  Settings, found by the admin's merchant search; empty parcel lists, labels and stats offer their next action.
- **Tested:** build 0 errors, 0 warnings; domain **182** (new: `ReturnListTests` 4, `ParcelRequestTests` 5,
  `PayoutAccountTests` 2); architecture 6. Integration: 3.4's full run **80 of 80**; then 3.5 to 3.8 targeted
  (22 of 22) and together in one full run of **87**: 85 passed, and two 3.2 tests failed because the new bell names
  flagged and partly delivered parcels on every page, so "this code is not in that tab" found it in the bell; they
  now read the list below the tabs. New: `ReturnListsTests` (2), `ParcelRequestsTests` (3), `PayoutAccountsTests` (3),
  `BellTests` (1), and sweep lines for `/Hub/Returns`, `/Rider/Return`, `/Merchant/Returns`, `/Merchant/Return`,
  `/Merchant/Requests`, `/Admin/Requests`, `/Merchant/Export` and `/Merchant/ByDate`. Both databases published.
- **Live on `OneDrop-Test`** at 1440 px and 390 px: returned OD10000519 at Mirpur and sent it on RL-100009 with Rafiq
  Hasan, handed it over from his phone, confirmed it as Fashion House with a note (the hub sees the note); asked to
  change OD10000516's cash from ৳450 to ৳400, approved it as the admin, saw the answer and the history line; kept a
  Nagad account without using it; delivered OD10000517 from the rider's phone and was paid now (INV-100245); the
  bell listed the delivery, the return and the list, then the payout; downloaded the CSV; by date; the merchant ID.
  No console errors, no sideways scroll at either width. Fixed on the way: "All 1 handed over", the CSV's money
  written two ways, an empty stage tab offering "Book a parcel", and Payments showing a bKash number as +880.
- **Not done:** the API cannot ask for a cancellation or cash change yet (it still cancels before pickup only); no
  limit on "Get paid now"; the bell does not follow a person to another browser.

### 2026-10-06 (Phase 2 finished as far as it can be — email, run sheet, reports)
- **Owner's request:** "Complete Phase 2", and "as we cant intigrate read SMS lets use mail insted", with a Gmail
  address and an app password. Asked and answered: the emails go to **merchants only** (recipients have no email
  address in our model, so they keep the fake SMS until there is a gateway), and the bKash / Nagad / bank payout
  gateways and the app's own SQL login are **left alone** ("we still dont have those so leave them").
- **The mail server (2.6, the half we can do).** `IEmailSender` with `SmtpEmailSender` (STARTTLS, one client per
  message) and `FakeEmailSender`; which one is used depends on whether `Email:Host` and `Email:From` are set. The
  Gmail address and its **app password live only in the git-ignored `src/Web/appsettings.Local.json`**; `appsettings.json`
  holds empty defaults, the README says how to fill them, and `WebAppFactory` forces the fake so a test run can never
  send real mail. New `/Dev/Emails` lists what was sent and has a button that sends a test email through the real
  server.
- **2.4 Merchant notifications.** `Payout.MarkPaid` now raises `PayoutPaid` (once, however often the gateway's answer
  is recorded); the outbox writes a `MerchantEmailMessage`, and `SendEmailsJob` — a third loop beside the texts and
  the webhooks — sends it. The email is composed at send time from the payout as it then is: cash collected, charges,
  what was sent, the account (last four digits only) and a link to the invoice. It goes to the merchant's contact
  email, or the account's first login if it has none; a merchant with neither is skipped rather than retried.
  `ITrackingLinks` gained `Invoice(number)`.
- **2.3 Run sheet.** `RunsHandler.SheetAsync` and `/Hub/RunSheet/{id}`: the parcels of the run **still to hand over**
  (a parcel the rider has recorded drops off), each with the door, the phone, the note, the attempt number and the
  cash, with totals and lines to sign. Printed from the page; offered in the toast as soon as parcels are assigned
  (`AssignResult` now carries the run id) and from the closing page while a rider is out.
- **2.5 Reports.** New `ReportsHandler` and `/Admin/Reports` over any period up to 92 days (a week by default):
  day by day (delivered, returned, cash collected, delivery / COD / return charges, earned, owed to merchants), by
  rider (stops, deliveries, success rate, cash collected against cash handed in, what is short, runs still open) and
  returns by merchant (rate and return charges). Each table downloads as CSV. The money comes from the ledger (whose
  `EntryDate` is already the courier's day) and the counts from the parcels that reached a final status, whose UTC
  `ClosedOn` is turned into the courier's day in memory — the window is capped at 92 days so that set stays small.
- **Tested:** build 0 errors, 0 warnings; domain **161** (one new assertion: a payout tells the merchant once);
  architecture 6. Integration: new `MerchantEmailTests`, `RunSheetTests` (2) and `ReportsTests` (2), plus two new
  sweep lines (`/Hub/RunSheet`, `/Admin/Reports`); the full run is recorded below. **The Gmail credentials were
  proved live** — an SMTP send from PowerShell was accepted, and the app's own sender delivered a test email through
  `smtp.gmail.com` from `/Dev/Emails`.
- **Worth knowing:** `perl -0pi -e` mangles any replacement containing `$"` (Perl reads it as a variable) — several
  C# interpolated strings were silently emptied before I noticed. Use the editor, not perl, for those.

### 2026-10-06 (later still: the gaps from Steadfast's panel, and moderators)
- **Owner's request:** ten screenshots of Steadfast's merchant panel — "Check these feature. We dont have a lot of
  them. These will help to improve our system" — then "list them down and remember them and start doing them one by
  one", with moderators first.
- **Listed:** the gap analysis became Phase 3 (3.1 to 3.8) above. Not taken: a prepaid wallet (Add fund / Check
  balance), Pick-n-Drop and "My incoming", and the Bangla switch.
- **3.1 Moderators, done.** A moderator is an ordinary merchant login plus a `Merchants.Moderator` row holding what
  they may do (`MerchantPermissions`: dashboard, parcels, booking, payments, tools, settings). The row hangs off the
  account's **main profile**, not a business, so one moderator works in every business of the account; it therefore
  carries `AccountId` and is not merchant-filtered (noted in Database.md). `/Merchant/Moderators` (owner only) lists
  them with inline tick boxes, adds one, gives out a new password, stops one and lets them back in.
- **How they are held:** `MerchantPermissionFilter` on the whole `/Merchant` folder reads the rights once per
  request, leaves them on `HttpContext.Items`, and refuses any page whose entry in `Web/Authentication/MerchantPages.cs`
  the person does not hold. The list is **default-deny**: a merchant page not on it is the owner's alone, so a page
  added later cannot quietly open itself. The menu, the favourites bar and the dashboard (quick actions, tools, the
  money card, latest parcels, the banners) only offer what the person may open, and the account card reads "Moderator".
- **Stopping one** archives the row and locks the login out (`IUserAccounts.SetLoginEnabledAsync`: `Archived`, lockout
  to `DateTimeOffset.MaxValue`, security stamp changed), so an open session ends and a fresh sign-in is refused.
- **No email yet**, so the first password is shown once on the page for the owner to hand over, and can be issued
  again; the person changes it at `/Account/Password`. Generated as capitals, letters and digits so it always passes
  the sign-in rules.
- **Tested:** build 0 errors, 0 warnings; domain **161** (four new in `ModeratorTests`), architecture 6; integration
  **68 of 68 over `ras-x2`, none skipped**, including two new `ModeratorsTests` (what a moderator may open, what is
  refused, permissions changed, stopped and shut out; and that no other account sees or stops them) and the sweep's
  new `/Merchant/Moderators` line. Both databases published (`Merchants.Moderator`, in the security policy).
- **Live on `OneDrop-Test`** at 1440 px and 390 px with headless Chrome: added a moderator as Fashion House, signed in
  as them, dashboard and parcels answered 200 while payments, booking, settings, API keys and the moderators page all
  went to `AccessDenied`; the sidebar showed four items instead of fifteen; permissions changed and the refusals
  followed. No sideways scroll at either width, no console errors.
- **Two things the screenshots caught**, both fixed: the dashboard still linked to pages a moderator may not open
  (the integration test found it first), and the permission ticks in the table were cramped — they are now two
  columns, with the "added" date folded under the email so the buttons are not pushed off the card.
- **Not done:** an emailed invitation code (there is no email sender; waits on 2.6), the courier admin cannot see an
  account's moderators, and the permission check is page-level, not inside each handler.

### 2026-10-06 (later: the coverage map the admin keeps)
- **Owner's request:** "The Coverage area and Hub area are now seeded data. Keep these but also add a option to make it
  dynamic so admin can add more or remove any of the existing." That is task 2.2, done without questions.
- **Decided, and worth knowing:** "remove" is **archive, never delete**. A deleted zone would orphan the parcels that
  were priced and routed by it (`Parcel.AreaId`, `PickupHubId`, `DeliveryHubId` all point at these rows), so a part of
  the map that comes off leaves the address lists, the API and the public map while its rows and history stay. The seed
  still lays the map out; nothing about it changed.
- **Domain:** `Hub`, `Zone` and `Area` gained `Create` / `Change` / `Archive` / `Restore` with their own validation
  (they had public constructors and no rules before). New `Domain/Network/Codes.cs` reads a hub or zone code: letters
  and digits, at most 20, held in capitals, because codes are printed on labels and read out over the phone.
- **Application:** new `CoverageAdminHandler` (list, add, edit, archive, restore for all three) with the guards, in the
  same spirit as the rider and pickup-point ones of this morning — nothing moves out from under work in flight:
  a hub closes only once its zones, riders, parcels and pickups have gone; a zone keeps its hub, city and suburb flag
  while parcels priced by them are still moving, and leaves the map only once its areas have; an area stays put while
  parcels are on their way there or a merchant collects from it; a zone or area comes back only onto an open parent.
  Codes are unique per courier and area names are unique per courier, each with a message instead of a database error.
- **UI:** `/Admin/Coverage` keeps the map it always showed as its first tab and gains **Hubs**, **Zones** and **Areas**,
  each a table with inline edit, an add form beside it and a confirm dialog on anything that comes off the map. The
  areas tab filters by zone (87 areas are too many to read at once) and the zones tab's area count links to it.
- **No schema change:** all three tables already carried `Archived`, so neither database needed a publish.
- **Tested:** build 0 errors, 0 warnings; domain 157 (11 new in `CoverageTests`), architecture 6; integration **66 of 66
  over `ras-x2`, none skipped**, including three new `CoverageAdminTests` (add a hub, a zone and an area and book to it
  through the API; the guards refuse and an empty area comes off; another courier can neither see it nor post to it)
  and the sweep's four Coverage lines. Live on the **dev** database at 1440 px and 390 px: opened a hub, drew a zone on
  it, added an area, saw all three on the map; the refusals appeared word for word ("Live check hub cannot close yet:
  1 zone it serves…", "Livepur still covers 1 area…", "Livepur Sadar sits in a zone the courier no longer covers…");
  then the area came off, the zone, and the hub closed. Four tabs, the zone filter narrowing 88 areas to 8, no sideways
  scroll at either width, no console errors. The live-check hub, zone and area were deleted from dev afterwards
  (16 hubs, 21 zones, 87 areas again).
- **Two things to know:** the first full run failed one assertion of mine — the sweep looked for "Mirpur hub", which the
  new add-hub form carries as a *placeholder*, not as another courier's data; the sweep now checks data-only strings.
  And two full runs in a row failed 11 and 41 tests purely on the `ras-x2` link dropping (pre-login handshake timeouts,
  "physical connection is not usable"), while `sqlcmd` answered instantly between them; the third run was clean. The
  link is flaky, not the code.
- **Not done:** an owner's look at the new tabs; hubs, zones and areas still cannot be imported in bulk.

### 2026-10-06
- **Owner's request (later): a picture for the main profile and each business, and a new footer credit.** Settings has an upload (and remove) for the business being worked in; new table `Merchants.MerchantPicture` (in the security policy, published to both databases), `MerchantPicture` entity that accepts only PNG, JPEG or WebP by their first bytes up to 512 KB, `MerchantPictureHandler`, `/Merchant/Picture/{id}` (404 for another account), shown in the sidebar, account menu, business switcher and businesses page. The footer now reads "Developed and designed by Talha from Octopi Digital" linking to octopi-digital.com (both layouts). Tests: five domain, two integration (`MerchantPictureTests`) and a sweep line.
- **Owner's request:** "Make our website UI also like this [Steadfast's panel] … this one looks much better", then "I
  like that favourite tab which stays in every page and user can customize them. Also the user setting panel at the
  top right side." Also: "use ras-x2 now" for the SQL Server.
- **Shell (every panel):** white sidebar with the signed-in account on top and sign out in red; the top bar carries the
  page title, the searches, the hide-amounts switch and the hotline (tap to call); larger type, rounder cards; number
  cards read icon and label, then the number, then context (CSS only, `display: contents`, so no page changed); the
  "next step" banner is light.
- **Merchant dashboard:** "Hello, {shop}"; "Right now" (each stage with its count and cash, a chevron to the tab) beside
  quick actions; tools and links; "Your last 30 days" (booked today and yesterday, booked in 30 days, delivery success,
  cash collected); the week; the money with the last payment; latest parcels. `MerchantDashboard` gained `Booked` and
  `LastPayout`. **Payments:** the next payout large, beside how it adds up (cash, delivery, COD and return charges).
- **Favourites and account menu:** the menu is now one list (`Display/Menu.cs`) that draws the sidebar, the stars and
  the favourites bar (merchant, admin, hub; up to eight; defaults per role; a cookie per login, `-` meaning none). The
  avatar opens the account menu: name, email, role, account pages, change password, sign out. New
  `/Account/Password` (any signed-in person), in the isolation sweep.
- **SQL Server:** both `*.Local.json` now use `ras-x2,1433` (resolves to 192.168.0.237, same instance, both databases
  there); docs updated.
- **Tested:** build 0 errors, 0 warnings; domain 137, architecture 6; integration 59 over `ras-x2`, none skipped — 58
  passed in the full run, and the favourites test, which had caught a real bug (unpinning everything brought the
  defaults back), passed after the fix (`AccountMenuTests` 2 of 2). Live on `OneDrop-Test` and dev at 1440 px and
  390 px: merchant dashboard, payments, admin dashboard, hub board, favourites (pin from a star, picker, reload keeps
  them), account menu, change password; no sideways scroll.
- **Two things went wrong on the way, both mine:** a viewport size I set on the DevTools tab while testing made the
  owner's view look cut off on the right; and starting the app with `--no-build` after editing `site.css` served
  browsers an empty stylesheet (stale gzip copy; now in Project-Context's traps). Both fixed.
- **Not done:** a Bangla switch and a dark theme (seen at Steadfast; product work);
  favourites do not follow a person to another browser (would need a column on the user).
- **Later (owner's request): several businesses in one merchant account**, as on Steadfast. Each business is a
  merchant row of its own (`Merchant.MainMerchantId` points an added one at the account's main profile), so parcels,
  pickups, pickup points, balance, payouts, API keys and webhook stay per business with no change to them. The login's
  claims list its other businesses; a cookie per login picks the one worked in and `ICurrentUser.MerchantId` returns
  it (a cookie naming anyone else's business is ignored). Approval and the payout account are the account's: set on the
  main profile and followed by every business (`FollowAccount`), from the merchant's settings or the admin's page.
  New `/Merchant/Businesses` (cards with each one's parcels on the way and money waiting, open another) and
  `/Merchant/NewBusiness`; a switcher under the account card in the sidebar and in the account menu; the admin's
  merchant list and page say which account a business belongs to. Schema published to both databases.
- **Tested:** build 0 warnings; domain 140 (three new), architecture 6; integration 61 over `ras-x2`, none skipped:
  60 passed in the full run, including the two new `MerchantBusinessesTests` (add, work in it, own parcels, payout
  account shared, forged cookie, suspension covers the account) and the sweep's two new lines; `FailedMessagesTests`
  failed because my live-check app on `OneDrop-Test` sent its planted text, and passed alone with the app stopped (now a
  trap). The first targeted run caught a 500 on every merchant page (the switcher partial's model type), fixed before
  the full run. Live on `OneDrop-Test` at about 1265 px: added "Fashion House Kids", landed in it with its own empty
  dashboard, switched back (the main profile kept its 6 parcels), businesses page, account menu switcher; no sideways
  scroll. The top bar squeezed the page title at that width; below 1400 px the searches narrow and the hotline is its
  icon.
- **Then (owner):** "Steadfast allows 10, then also make ours 10"; and no login per business: "I already logged in to
  get into the merchant so I don't need other login". The limit is a courier setting, `Tenant.MaxBusinessesPerAccount`
  (NULL: none), set to 10 for OneDrop Courier by `003_BusinessesPerAccount.sql`; the main profile does not count, as on
  Steadfast ("0 of 10 businesses"). `Merchant.AddBusiness` refuses the eleventh; the businesses page says "n of 10
  businesses added" and drops "Add business" when full; the add page explains instead of offering a form. Published to
  both databases (the test-only "rival" courier has no limit).
- **Tested:** build 0 errors, 0 warnings; domain 141 (one new: up to the limit, refused at it, no limit when NULL),
  architecture 6; integration 61 of 61 over `ras-x2`, none skipped (the businesses page reads "1 of 10 businesses
  added"). A first run hung for 17 minutes after the connection to `ras-x2` dropped at 11:53 ("physical connection is
  not usable"); stopped and run again.
- **Then (owner):** "make the unactive business in gray color". In the business switcher (sidebar and account menu)
  the businesses not being worked in now have a grey tile and grey name; the open one keeps its sky blue. CSS only;
  the rebuilt app serves the new rules.
- **Then (owner):** the favourites bar ran out of room before eight tabs. Each chip now shrinks as more are pinned
  (`:has(.fav-chip:nth-child(6|7|8))`, counted on the chips because they are the first children of `.favbar-items`):
  at six the chips are 35 px and 13 px, at seven 33 px and 12.5 px with the "Favourites" word dropped (the star stays),
  at eight 32 px and 12 px; below 1400 px a full bar tightens once more. CSS only, so nothing had to re-render; the
  overflow scroll stays as the last resort. Checked at 1920, 1440 and 1280 px with five, six, seven and eight chips:
  all eight fit on one line beside the 256 px sidebar at every one of them. Build 0 errors, 0 warnings; the rebuilt app
  serves the new rules.

### 2026-10-05 (later: Steadfast's panel, then reweighing at the hub)
- **Owner's request:** look at Steadfast's merchant dashboard in Chrome and make ours as easy to use, without copying it.
- **Getting at it:** Chrome 136+ ignores `--remote-debugging-port` against the default profile, by design, so it was
  driven over DevTools with its own `--user-data-dir` (`.chrome-debug`, excluded in `.git/info/exclude`) and a
  one-time sign-in. A screenshot had suggested their top bar held a customer fraud check; the DOM showed it is a parcel
  search with a field selector (Parcel ID / Phone / Invoice / Tracking code) plus a prepaid **balance** button, so that
  reading was wrong. Their order list and add-parcel form could not be read: the account is unverified, so it has no
  parcels and `/add-parcel` redirects to the KYC form.
- **Taken from them:** cash shown beside every stage count; a hide-amounts toggle (theirs defaults to hidden, ours to
  shown); banners that carry the button that fixes them. **Not taken:** the field selector (our one box already searches
  code, phone and name), the menu search and favourites bar (both answer a 30-item sidebar we do not have). **Noted as
  product gaps, not UI:** several businesses per login, moderators, payment request, prepaid funds, WhatsApp updates.
- **Merchant and admin panels:** a customer check in the merchant top bar (`/Merchant/FraudCheck` on GET, my own idea,
  not theirs); per-stage cash on the merchant tiles ("to collect" while on the way, "collected" once delivered) and on
  the admin's "Parcels in progress"; a hide-amounts switch in the top bar, read before first paint so nothing flashes —
  one change to `Money.Taka` (the digits moved into `.amount-figure`) made every taka in the app maskable; pending and
  suspended banners gained "Add your payout account" and a tap-to-dial hotline; quick-action tiles on the merchant
  dashboard, mainly for phones where the sidebar is behind the menu button.
- **Logic gap closed — nobody could weigh a parcel.** `WeightGrams` was whatever the merchant typed and froze at
  pickup, so a 5 kg parcel booked as 500 g travelled for ৳60 for ever. A hub now weighs it on the parcel page
  (`Parcel.Reweigh`): the delivery charge is worked out again on the scale's reading, the merchant's own figure is
  kept, and the history records both weights and both charges. Repricing from today's rate card would have broken
  "charges snapshot at booking" (`DeliveryRate.Change` edits the row in place, so there is no rate history), so the
  parcel now carries the weight rule it was booked under — `IncludedWeightGrams`, `BaseCharge`, `ExtraKgCharge` on
  `ParcelCharges` and the table, backfilled by `Scripts/Pre/002_ParcelWeightRule.sql`.
- **Two failures fixed that were already on `main`** from `ce576c1`, not from this work: the isolation sweep had no
  line for `/Admin/Vehicles`, and `RowLevelSecurityTests` planted a rider with a raw INSERT written before the NOT NULL
  `Vehicle` column, so it died on that instead of reaching the block predicate.
- **Tested:** build 0 errors, 0 warnings; domain 137 (five new: the charge follows the scale, a rate change since
  booking does not reach it, only while the courier has it to deliver, and two refused weights), architecture 6. Both
  databases published. Integration run recorded below.
- **Worth knowing:** `dotnet test --project tests/...` reports "Zero tests ran" (exit 5) on this machine; the suites
  run when the test executables are invoked directly. The documented command in Conventions.md does not work as written.

### 2026-10-05
- **Owner's request:** "make the UI more beautiful … professional … add beautiful animation … make the whole system user
  friendly … also find if there is any logical gap then fix them." Done without questions (owner asleep).
- **UI:** `site.css` rewritten on the same class names: Plus Jakarta Sans and JetBrains Mono (`_Fonts.cshtml`), refined
  tokens and shadows, a glowing active-menu bar, lifted hover cards, numbers that count up, bars and progress that grow,
  a breathing "Live" dot and changed numbers flashing on live refresh, all on first paint only and off for reduced
  motion. New `site.js`: busy spinners and no double posts, a styled confirm dialog (`data-confirm`, replacing every
  `confirm()` and added to stop rider, run payouts, cancel pickup, return parcel, new webhook secret, stop webhook),
  success toasts, dismissible tips, "/" to search, show password, copy tracking code.
- **Guidance:** merchant "Get started" checklist (approval, payout account, first parcel, first handover) then a "next
  step" banner when parcels wait without a pickup; hub board as the day's six numbered steps with counts and links;
  tips on assign, runs, pickups; sidebar "how it works" per role; tracking page says in one sentence what happens next.
- **Screens:** scan page sends scans without reloading, plays a tone per result (high done, low problem; can be muted),
  keeps the session's scans and catches keystrokes for the scanner; assign page counts ticked parcels and "select all"
  skips parcels whose customer asked for a later day (marked in the row, also on the board); rider closing asks before
  closing with cash short; rider app shows day progress and a Map button, and asks for a partial-delivery reason only
  when less was collected; pickups warn when nothing is booked at the point and fill the count; booking form links a
  phone to its fraud check and flashes changed charges; sign-in is a split screen; landing page has a moving route and
  journey.
- **Logic gaps closed:** (1) stopping a rider, or moving them to another hub, while they carry parcels, have an open
  run or an assigned pickup is refused with what is open (their parcels would be stuck: the rider app shows nothing to
  a stopped rider and the run closes at the old hub). (2) A pickup point cannot move to another zone while parcels or a
  pickup wait there (they were priced and routed by the old zone). (3) `Parcel.Hold` refuses a "deliver on" day that is
  not after today; the date picker starts tomorrow.
- **Tested:** build 0 errors, 0 warnings (two old xUnit2029 warnings in `DatabaseProjectFileTests` fixed); domain 111
  (one new), architecture 6, integration 55 (two new in `GuardTests`), none skipped. Live check on `OneDrop-Test` with
  demo logins (the dev database has no demo users): every role's main pages at 1366 px and 390 px, no script errors, no
  sideways scroll; scanned five labels without reloads, assigned three parcels (toast), the stop-rider dialog and its
  refusal, a partial delivery's reason appearing. Screenshots showed the hero route crossing the text, the scan
  placeholder in capitals, the sign-in art over the role list and long demo e-mails overflowing; all fixed and
  re-checked. Six parcels for the demo shop "Fashion House" were left in `OneDrop-Test`.
- **Not done:** sound on a real hand scanner; phone screenshots of the admin pages.
- **Later the same day (owner's request):** "I want my landing page also like [Steadfast's] but not copy paste … a beautiful
  graphical cartoon type animation … change our color theme, it looks like copy of steadfast courier … make a good
  business logo." A first draft in ultraviolet was stopped by the owner: "dont use ultraviolet color, that color makes
  it feel like AI generated. Use some other color theme and the coloring minimal."
- **Brand:** logo = a drop-shaped map pin holding a taped parcel, landing on a ripple, on a marigold tile
  (`_BrandMark.cshtml` in pages; `wwwroot/images/logo.svg` and `logo-on-dark.svg` with the word outlined from
  Bricolage Grotesque; `icons/icon.svg`, `icon-192.png`, `icon-512.png` and a new full-bleed `icon-maskable-512.png`).
  Theme = ink `#17181c` on warm paper `#f7f6f3`, one accent marigold `#f2a900`; primary buttons solid ink, `btn-accent`
  for the few marketing calls to action; every gradient and glow removed; "out for delivery" is an ink badge; charts
  grey, ink and red. All of it through the tokens in `site.css`, so every panel changed with it.
- **Front page:** a little planet (shops, a hub, homes, a rickshaw, krishnachura trees, a route map on its face) turning
  under a rider on a scooter, parcels on parachutes and status cards following one parcel (`Art/_Planet.cshtml`, three
  layers so the planet turns on the compositor); a ribbon of services; four animated steps (book, pick up, hub belt
  and scanner, cash into the wallet); services; rate card (inside city in ink); a closing pin that drops on a ripple;
  a real footer. `landing.css` is loaded by the front page only; `site.js` pauses a drawing off screen and lets
  sections below the fold rise in. Tracking page uses the same tracking box.
- **Tested:** build 0 errors, 0 warnings; domain 111, architecture 6, integration 57 (two new in `PortalPagesTests`:
  the front page's drawings, tracking box and rates; every icon in the rider manifest exists), none skipped, two full
  green runs in a row. One earlier run had `/Admin` answer 500 once after 13 s in `Each_role_lands_on_its_own_home`
  (only a class name changed there; not reproduced in three more runs; looks like a database timeout under parallel
  load). Live: front page at 1440 px and 390 px (no sideways scroll, no script errors), merchant, admin and hub panels
  at 1440 px, rider app at 390 px, sign-in, sign-up and tracking on `OneDrop-Test` with demo logins. Screenshots showed
  birds flying over the headline, a gap after the price in rate lists (old), the tracking progress bar turned into
  cards by a clashing `.step` class, the tracking placeholder cut off on phones and the hub tip still saying "orange";
  all fixed and re-checked.
- **Not done:** a look on a real phone and in Safari; the intermittent `/Admin` 500 is not explained.
- **Then (owner's review):** "I don't like the yellow color theme … try something light color like sky blue." The
  accent is now sky blue `#7cc6f2` (`--accent-text` `#0b6a9e` for links and text, `--accent-soft` `#d9effc`), and the
  neutrals moved from warm paper to cool light grey (`--bg` `#f5f7fa`, ink `#141820`) because cream beside sky blue
  looked muddy in screenshots. Logo tile, app icons and logo files regenerated in sky blue; drawings' second tone light
  sky `#c6e7fb`; the sun is white with sky rays; the dev strip and the "needs you" stat cards follow the accent; the
  hub tip says "a blue number". Tested: build 0 warnings; 111 + 6 + 57 pass, none skipped; live at 1440 px and 390 px
  (front page, merchant panel), no sideways scroll or script errors.
- **Then:** the owner asked for a more professional headline. Now "Reliable delivery. Next-day payouts." (58 px, was
  62 px) with a plainer lead and the label "Courier service for online businesses"; checked at 1440 px and 390 px.

### 2026-10-04
- **Decided with the owner:** the pivot above.
- **Done:** phase 1 (tasks 1.1–1.8). Both databases dropped (no sessions open) and deployed from nothing with the new
  dacpac and seed.
- **Tested:** build 0 errors, 0 warnings; 109 domain + 6 architecture + 50 integration = 165 pass, none skipped.
  The integration run found two EF queries that grouped then joined (admin payouts, admin dashboard: 500), now
  aggregated first; public tracking showed the collected amount through an event note, now each step shows its status
  only; the out-for-delivery text is correctly skipped once the parcel is delivered (the test now sends between
  steps). Live on dev: every page answered 200 for its role (public, merchant, hub, rider, admin, Dev), the sample
  parcels' texts went out, no errors in the log. Screenshots at 1366 px and 390 px (DevTools emulation) of 15 pages,
  no sideways scroll at phone width; they showed the app shell broken on desktop (the menu scrim label took a grid
  cell), the search icon over its placeholder, the brand label wrapping, a poor ৳ glyph, a tiny tracking-code heading,
  the rider's phone in +880 form and "Attempt 0 of 3" while out; all fixed and re-checked.
- **Not done:** a mutation run of the isolation sweep; scanning a printed label with a real hand scanner or phone.
- **Later the same day (owner's review):** branch pushed and fast-forwarded into local `main` (not pushed). Demo data
  removed from the dev database (parcels, riders, demo merchants and logins; hubs, zones, areas, rates, both admins and
  the owner's own sign-up kept) and `Seed:DemoData`/`DemoActivity` turned off in `appsettings.Development.json`.
  The owner found the admin's "Pickups open" tile opened Barishal (first by name) while the pickup was at Mirpur.
  Locking the admin to one hub was considered and rejected (the admin must see the whole courier); instead hub pages
  ask "Choose your hub" (every hub with its waiting work, busiest first) until one is chosen, the choice is forgotten
  at sign-in and sign-out, the dashboard tiles open the one hub with that work (or the chooser), and the hub menu shows
  each hub's waiting count with an "All hubs" link. Tested: build 0 warnings; 109 + 6 + 51 = 166 pass; live on dev as
  admin: `/Hub/Pickups` → chooser, "Pickups open" → Mirpur directly, menu shows "Mirpur hub (MIR) · 1 waiting".
  Not done: screenshots at phone width.
- **Later still (owner's review):** a parcel is now texted a tracking link the moment it is booked, not only when it goes
  out for delivery, so the recipient can follow it from the start. `Parcel.Create` raises a new `ParcelBooked` event,
  kept separate from `ParcelStatusChanged` so booking never posts to the merchant's webhook (that still starts at
  pickup); the outbox writes it as the existing `RecipientTextMessage` with status `Pending`, and `RecipientTexts`
  gained a case for it (dropped if the parcel has already moved on by the time it is sent, same as the others).
  Tested: build 0 warnings; 110 + 6 + 52 = 168 pass (one new domain test, one new and one adjusted integration test);
  live on dev: booked a parcel as a merchant, the tracking SMS appeared in `/Dev/Sms` with a working `/Track` link.
  Test merchant and parcel removed afterwards.
- **Next:** the owner's review (2.1).
