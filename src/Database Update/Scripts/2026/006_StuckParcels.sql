-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 006_StuckParcels.sql
-- Purpose: OneDrop Courier tells its admin about a parcel that has stayed where it is for more than 3 days: with a
--          pickup rider, at a hub, between hubs, on hold past its day or going back to the merchant. Parcels booked
--          before Parcels.Parcel.MovedOn existed take the time of their latest history line, the nearest record of
--          their last move (a later note only makes a parcel look less stuck, never more).
-- Author: Courier team
-- Date: 2026-10-12
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
UPDATE Platform.Tenant
SET
    StuckAfterDays = 3
WHERE
    Slug = N'onedrop';

UPDATE Parcel
SET
    MovedOn = COALESCE(
        (
            SELECT
                MAX(Event.Created)
            FROM
                Parcels.ParcelEvent Event
            WHERE
                Event.ParcelId = Parcel.Id
        ),
        Parcel.Created)
FROM
    Parcels.Parcel Parcel;
