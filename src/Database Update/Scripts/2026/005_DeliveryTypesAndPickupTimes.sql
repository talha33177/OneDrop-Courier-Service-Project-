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
--       2026-10-13 The extras moved to Pricing.DeliveryOptionCharge and the columns this set are gone, so a new
--                  database could no longer run it: the delivery types are now seeded by 008_DeliveryOptions.sql.
--                  Databases that ran it keep what it set (Scripts/Pre/004 copied it across)
--       2026-10-13 The 3:00 pm cut-off is gone too (Scripts/Pre/005_DropPickupCutoff.sql): the van takes what is
--                  booked until it reaches each pickup point. Only the 3:30 pm pickup time is set here now
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
DECLARE @Courier BIGINT = (SELECT Id FROM Platform.Tenant WHERE Slug = N'onedrop');

UPDATE Platform.Tenant
SET
    PickupTime = '15:30'
WHERE
    Id = @Courier;
