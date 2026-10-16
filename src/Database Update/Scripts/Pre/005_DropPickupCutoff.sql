-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 005_DropPickupCutoff.sql
-- Purpose: The courier no longer has a pickup cut-off. The pickup van visits each pickup point once a day and takes
--          whatever is waiting when it gets there: a parcel booked before the van reaches the point goes with that
--          day's pickup, one booked after goes with the next day's. Platform.Tenant.PickupCutoff (the 3:00 pm of the
--          first daily pickup) is dropped; PickupTime, when the truck leaves the hub, stays. Runs before the publish,
--          against the old schema; does nothing on a new database or once it is done.
-- Author: Courier team
-- Date: 2026-10-13
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
IF OBJECT_ID(N'Platform.Tenant', N'U') IS NOT NULL AND
   COL_LENGTH(N'Platform.Tenant', N'PickupCutoff') IS NOT NULL
BEGIN
    ALTER TABLE Platform.Tenant DROP COLUMN [PickupCutoff];
END;
