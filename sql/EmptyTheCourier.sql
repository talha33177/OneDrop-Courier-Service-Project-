-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: EmptyTheCourier.sql
-- Purpose: Empties one courier of everything that happened on it and leaves the site ready to open again: its
--          merchants, businesses, moderators, API keys, pickup points, parcels and their history, riders, runs,
--          attempts, pickups, return lists, the ledger, payouts, online payments and the outbox all go, with every
--          login but the courier's super admins. What the courier is set up with stays: the courier itself and its
--          settings, its hubs, zones and areas, the rate card, the delivery types and the vehicles. Tracking, invoice,
--          payment and return list numbers start again when no courier on the server has any left.
--
--          Ad-hoc, by hand, never deployed: it is not a DbUp script, so it runs only when someone runs it, and never
--          by a deploy. It cannot be undone. Take a backup first (Documentation/Deployment.md, "Backups").
--
--          Run it against the site's own database, with the courier's slug below:
--            sqlcmd -S <server> -d <database> -U <user> -P <password> -C -I -i sql\EmptyTheCourier.sql
--          Nothing is written unless every step succeeds: one transaction, and any error rolls all of it back.
-- Author: Courier team
-- Date: 2026-10-16
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Slug NVARCHAR (100) = N'onedrop';

DECLARE @Tenant BIGINT = (SELECT Id FROM Platform.Tenant WHERE Slug = @Slug);
IF @Tenant IS NULL
BEGIN
    THROW 50000, 'There is no courier with that slug in this database. Check @Slug and the database name.', 1;
END;

-- PRINT takes no subquery, so the counts are read into variables first
DECLARE @Merchants INT = (SELECT COUNT(*) FROM Merchants.Merchant WHERE TenantId = @Tenant);
DECLARE @Parcels INT = (SELECT COUNT(*) FROM Parcels.Parcel WHERE TenantId = @Tenant);
DECLARE @Riders INT = (SELECT COUNT(*) FROM Delivery.Rider WHERE TenantId = @Tenant);
DECLARE @Logins INT = (SELECT COUNT(*) FROM [Identity].[User] WHERE TenantId = @Tenant);

PRINT CONCAT('Emptying the courier ', @Slug, ' in ', DB_NAME(), '.');
PRINT CONCAT('  merchants ', @Merchants, ', parcels ', @Parcels, ', riders ', @Riders, ', logins ', @Logins);

BEGIN TRANSACTION;

-- The logins that go: every one of this courier's but its super admins, who are the admin panel
CREATE TABLE #Going (UserId BIGINT PRIMARY KEY);

INSERT INTO #Going
    (UserId)
SELECT
    U.Id
FROM
    [Identity].[User] U
WHERE
    U.TenantId = @Tenant AND
    NOT EXISTS (
        SELECT 1
        FROM
            [Identity].UserRole UR
            JOIN
            [Identity].[Role] R
                ON R.Id = UR.RoleId
        WHERE
            UR.UserId = U.Id AND
            R.NormalizedName = N'TENANTADMIN');

IF NOT EXISTS (SELECT 1 FROM [Identity].[User] U WHERE U.TenantId = @Tenant AND U.Id NOT IN (SELECT UserId FROM #Going))
BEGIN
    THROW 50001, 'This courier has no super admin login, so emptying it would leave nobody who can sign in. Make one first.', 1;
END;

-- Messages waiting to be sent, about things that are going
DELETE FROM Notifications.OutboxMessage WHERE TenantId = @Tenant;

-- Money: payments and the ledger before the payouts and parcels they point at
DELETE FROM Payments.OnlinePayment WHERE TenantId = @Tenant;
DELETE FROM Payments.PayoutRequest WHERE TenantId = @Tenant;
DELETE FROM Payments.LedgerEntry WHERE TenantId = @Tenant;
DELETE FROM Payments.Payout WHERE TenantId = @Tenant;

-- The riders' work: return lists, runs, attempts and pickups before the parcels and riders they name
DELETE FROM Delivery.ReturnListParcel WHERE TenantId = @Tenant;
DELETE FROM Delivery.ReturnList WHERE TenantId = @Tenant;
DELETE FROM Delivery.DeliveryAttempt WHERE TenantId = @Tenant;
DELETE FROM Delivery.DeliveryRun WHERE TenantId = @Tenant;
DELETE FROM Delivery.PickupRequest WHERE TenantId = @Tenant;

-- The parcels and their history
DELETE FROM Parcels.ParcelRequest WHERE TenantId = @Tenant;
DELETE FROM Parcels.ParcelEvent WHERE TenantId = @Tenant;
DELETE FROM Parcels.Parcel WHERE TenantId = @Tenant;

DELETE FROM Delivery.Rider WHERE TenantId = @Tenant;

-- A merchant account's own rows, before the logins they name and the merchants they hang off
DELETE FROM Merchants.MerchantApiKey WHERE TenantId = @Tenant;
DELETE FROM Merchants.MerchantPicture WHERE TenantId = @Tenant;
DELETE FROM Merchants.MerchantPayoutAccount WHERE TenantId = @Tenant;
DELETE FROM Merchants.Moderator WHERE TenantId = @Tenant;
DELETE FROM Merchants.PickupPoint WHERE TenantId = @Tenant;

-- What is kept remembers who last changed it, which may be a login that is going
UPDATE Platform.Tenant           SET UpdatedId = NULL WHERE Id = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);
UPDATE Network.Hub               SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);
UPDATE Network.Zone              SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);
UPDATE Network.Area              SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);
UPDATE Pricing.DeliveryRate      SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);
UPDATE Pricing.DeliveryOption    SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);
UPDATE Pricing.DeliveryOptionCharge SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);
UPDATE Delivery.VehicleCapacity  SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);

-- The merchants go after their logins (a merchant's login points at its merchant), so who last changed a merchant
-- is forgotten first, or that login could not go
UPDATE Merchants.Merchant        SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM #Going);

-- The logins themselves. A super admin has no staff row, so every staff row of this courier goes with them
DELETE FROM [Identity].StaffMember WHERE TenantId = @Tenant;
DELETE FROM [Identity].UserToken WHERE UserId IN (SELECT UserId FROM #Going);
DELETE FROM [Identity].UserLogin WHERE UserId IN (SELECT UserId FROM #Going);
DELETE FROM [Identity].UserClaim WHERE UserId IN (SELECT UserId FROM #Going);
DELETE FROM [Identity].UserRole WHERE UserId IN (SELECT UserId FROM #Going);
DELETE FROM [Identity].[User] WHERE Id IN (SELECT UserId FROM #Going);

-- The businesses added to an account before the main profiles they point at
DELETE FROM Merchants.Merchant WHERE TenantId = @Tenant AND MainMerchantId IS NOT NULL;
DELETE FROM Merchants.Merchant WHERE TenantId = @Tenant;

-- Numbers start again, but only when no courier on this server still holds one of them
IF NOT EXISTS (SELECT 1 FROM Parcels.Parcel)
BEGIN
    ALTER SEQUENCE Parcels.TrackingNumber RESTART WITH 10000001;
END;

IF NOT EXISTS (SELECT 1 FROM Payments.Payout)
BEGIN
    ALTER SEQUENCE Payments.PayoutNumber RESTART WITH 100001;
END;

IF NOT EXISTS (SELECT 1 FROM Payments.OnlinePayment)
BEGIN
    ALTER SEQUENCE Payments.OnlinePaymentNumber RESTART WITH 100001;
END;

IF NOT EXISTS (SELECT 1 FROM Delivery.ReturnList)
BEGIN
    ALTER SEQUENCE Delivery.ReturnListNumber RESTART WITH 100001;
END;

DROP TABLE #Going;

COMMIT TRANSACTION;

PRINT 'Emptied. What is left for this courier:';
SELECT
    (SELECT COUNT(*) FROM [Identity].[User] WHERE TenantId = @Tenant) AS Logins,
    (SELECT COUNT(*) FROM Merchants.Merchant WHERE TenantId = @Tenant) AS Merchants,
    (SELECT COUNT(*) FROM Parcels.Parcel WHERE TenantId = @Tenant) AS Parcels,
    (SELECT COUNT(*) FROM Delivery.Rider WHERE TenantId = @Tenant) AS Riders,
    (SELECT COUNT(*) FROM Network.Hub WHERE TenantId = @Tenant) AS Hubs,
    (SELECT COUNT(*) FROM Network.Zone WHERE TenantId = @Tenant) AS Zones,
    (SELECT COUNT(*) FROM Network.Area WHERE TenantId = @Tenant) AS Areas,
    (SELECT COUNT(*) FROM Pricing.DeliveryRate WHERE TenantId = @Tenant) AS Rates,
    (SELECT COUNT(*) FROM Pricing.DeliveryOption WHERE TenantId = @Tenant) AS DeliveryTypes;
