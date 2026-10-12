-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 005_DeliveryTypesAndPickupTimes.sql
-- Purpose: OneDrop Courier's delivery types and its daily pickup. Express costs ৳40 more and is delivered the day
--          after pickup, inside the city and to a suburb (not offered outside the city). What is inside adds ৳10 for
--          liquid, ৳20 for fragile, ৳30 for fresh food and ৳50 for frozen, the owner's order: fragile above regular,
--          frozen above fragile. Fresh food and frozen go inside the city and to a suburb only, since they cannot
--          wait days on the road. The pickup truck leaves each hub at 3:30 pm and collects the parcels booked before
--          3:00 pm; a parcel booked later goes the next day (owner, 2026-10-09). The courier's admin changes the
--          extras on the Rates page.
-- Author: Courier team
-- Date: 2026-10-09
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
DECLARE @Courier BIGINT = (SELECT Id FROM Platform.Tenant WHERE Slug = N'onedrop');

UPDATE Pricing.DeliveryRate
SET
    ExpressCharge = CASE ServiceArea WHEN 3 THEN NULL ELSE 40 END,
    ExpressDays = CASE ServiceArea WHEN 3 THEN NULL ELSE 1 END,
    LiquidCharge = 10,
    FragileCharge = 20,
    FreshFoodCharge = CASE ServiceArea WHEN 3 THEN NULL ELSE 30 END,
    FrozenCharge = CASE ServiceArea WHEN 3 THEN NULL ELSE 50 END
WHERE
    TenantId = @Courier;

UPDATE Platform.Tenant
SET
    PickupCutoff = '15:00',
    PickupTime = '15:30'
WHERE
    Id = @Courier;
