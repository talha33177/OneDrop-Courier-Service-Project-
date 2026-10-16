-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 014_EmptyTheLiveSite.sql
-- Purpose: The live site (delyvo.octopi-digital.com) was started with the demo seeder on, so it opened with made-up
--          merchants, riders and parcels that its admin cannot clear from the panel: a rider holding a parcel cannot
--          be stopped, and the demo logins' password is not one the owner has. This empties that site once of
--          everything that happened on it, leaving it as a courier ready to open: the courier and its settings, its
--          hubs, zones and areas, the rate card, the delivery types, the vehicles and its super admin logins stay;
--          merchants, businesses, moderators, API keys, pickup points, parcels and their history, riders, runs,
--          attempts, pickups, return lists, the ledger, payouts, online payments, the outbox and every other login go,
--          and tracking, invoice, payment and return list numbers start again.
--
--          ONLY the live database, which is named Delyvo: every other database (the development OneDrop, the test
--          OneDrop-Test, a new one deployed from nothing) is left exactly as it is, so a deploy anywhere else does
--          nothing here. DbUp runs a script once per database, so the live site is emptied on the first deploy that
--          carries this script and never again: the merchants who sign up afterwards are safe.
--
--          The demo data comes back at every start while the site runs in Development with Seed:DemoData on, so
--          Deploy-Iis.ps1 sets the site's app pool to Production with the demo seed off before this runs.
-- Author: Courier team
-- Date: 2026-10-16
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
IF DB_NAME() <> N'Delyvo'
BEGIN
    PRINT CONCAT('Not the live site (this database is ', DB_NAME(), '), so nothing was emptied.');
END
ELSE
BEGIN
    DECLARE @Tenant BIGINT = (SELECT Id FROM Platform.Tenant WHERE Slug = N'onedrop');
    DECLARE @Going TABLE (UserId BIGINT PRIMARY KEY);

    -- The logins that go: every one of the courier's but its super admins, who are the admin panel
    INSERT INTO @Going
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

    -- Nothing is emptied unless a super admin would be left to sign in afterwards
    IF @Tenant IS NULL OR
        NOT EXISTS (
            SELECT 1
            FROM [Identity].[User] U
            WHERE U.TenantId = @Tenant AND U.Id NOT IN (SELECT UserId FROM @Going))
    BEGIN
        PRINT 'The courier is not there, or it has no super admin login, so nothing was emptied.';
    END
    ELSE
    BEGIN
        -- PRINT takes no subquery, so the counts are read into variables first
        DECLARE @Merchants INT = (SELECT COUNT(*) FROM Merchants.Merchant WHERE TenantId = @Tenant);
        DECLARE @Parcels INT = (SELECT COUNT(*) FROM Parcels.Parcel WHERE TenantId = @Tenant);
        DECLARE @Riders INT = (SELECT COUNT(*) FROM Delivery.Rider WHERE TenantId = @Tenant);
        DECLARE @Logins INT = (SELECT COUNT(*) FROM [Identity].[User] WHERE TenantId = @Tenant);
        DECLARE @GoingCount INT = (SELECT COUNT(*) FROM @Going);

        PRINT CONCAT('Emptying the live site: merchants ', @Merchants, ', parcels ', @Parcels,
                     ', riders ', @Riders, ', logins ', @GoingCount, ' of ', @Logins);

        -- Messages waiting to be sent, about things that are going
        DELETE FROM Notifications.OutboxMessage WHERE TenantId = @Tenant;

        -- Money: the payments and the ledger before the payouts and parcels they point at
        DELETE FROM Payments.OnlinePayment WHERE TenantId = @Tenant;
        DELETE FROM Payments.PayoutRequest WHERE TenantId = @Tenant;
        DELETE FROM Payments.LedgerEntry WHERE TenantId = @Tenant;
        DELETE FROM Payments.Payout WHERE TenantId = @Tenant;

        -- The riders' work, before the parcels and riders it names
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
        UPDATE Platform.Tenant              SET UpdatedId = NULL WHERE Id = @Tenant AND UpdatedId IN (SELECT UserId FROM @Going);
        UPDATE Network.Hub                  SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM @Going);
        UPDATE Network.Zone                 SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM @Going);
        UPDATE Network.Area                 SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM @Going);
        UPDATE Pricing.DeliveryRate         SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM @Going);
        UPDATE Pricing.DeliveryOption       SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM @Going);
        UPDATE Pricing.DeliveryOptionCharge SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM @Going);
        UPDATE Delivery.VehicleCapacity     SET UpdatedId = NULL WHERE TenantId = @Tenant AND UpdatedId IN (SELECT UserId FROM @Going);

        -- The logins themselves. A super admin has no staff row, so every staff row of this courier goes with them
        DELETE FROM [Identity].StaffMember WHERE TenantId = @Tenant;
        DELETE FROM [Identity].UserToken WHERE UserId IN (SELECT UserId FROM @Going);
        DELETE FROM [Identity].UserLogin WHERE UserId IN (SELECT UserId FROM @Going);
        DELETE FROM [Identity].UserClaim WHERE UserId IN (SELECT UserId FROM @Going);
        DELETE FROM [Identity].UserRole WHERE UserId IN (SELECT UserId FROM @Going);
        DELETE FROM [Identity].[User] WHERE Id IN (SELECT UserId FROM @Going);

        -- The businesses added to an account before the main profiles they point at
        DELETE FROM Merchants.Merchant WHERE TenantId = @Tenant AND MainMerchantId IS NOT NULL;
        DELETE FROM Merchants.Merchant WHERE TenantId = @Tenant;

        DECLARE @LeftLogins INT = (SELECT COUNT(*) FROM [Identity].[User] WHERE TenantId = @Tenant);
        DECLARE @LeftHubs INT = (SELECT COUNT(*) FROM Network.Hub WHERE TenantId = @Tenant);
        DECLARE @LeftAreas INT = (SELECT COUNT(*) FROM Network.Area WHERE TenantId = @Tenant);
        DECLARE @LeftRates INT = (SELECT COUNT(*) FROM Pricing.DeliveryRate WHERE TenantId = @Tenant);
        DECLARE @LeftTypes INT = (SELECT COUNT(*) FROM Pricing.DeliveryOption WHERE TenantId = @Tenant);

        PRINT CONCAT('Emptied. Left: logins ', @LeftLogins, ', hubs ', @LeftHubs, ', areas ', @LeftAreas,
                     ', rates ', @LeftRates, ', delivery types ', @LeftTypes);
    END;
END;

-- The numbers start again once no courier in this database holds one of them, whichever database this is
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
