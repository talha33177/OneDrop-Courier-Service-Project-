-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 003_DeliveryTypesAndPickups.sql
-- Purpose: Two changes SqlPackage cannot make to tables with rows. Parcels now go regular or express and say what is
--          inside (Parcels.Parcel.Speed, ProductType, SpeedCharge and ProductCharge, NOT NULL with no default):
--          parcels booked before them went regular with general goods and no extra, which is what they are given.
--          Pickups are now scheduled by the day's cut-off instead of asked for, so Delivery.PickupRequest loses the
--          merchant's ExpectedParcels and Note (and the check on ExpectedParcels). Runs before the publish, against
--          the old schema; each part does nothing on a new database or once it is done.
-- Author: Courier team
-- Date: 2026-10-09
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
IF OBJECT_ID(N'Parcels.Parcel', N'U') IS NOT NULL AND
   COL_LENGTH(N'Parcels.Parcel', N'Speed') IS NULL
BEGIN
    ALTER TABLE Parcels.Parcel ADD
        [Speed]         TINYINT         NULL,
        [ProductType]   TINYINT         NULL,
        [SpeedCharge]   DECIMAL (10, 2) NULL,
        [ProductCharge] DECIMAL (10, 2) NULL;

    -- The new columns are not known when this batch is compiled, so the rest runs as its own batch
    EXEC (N'
        UPDATE Parcels.Parcel
        SET
            [Speed] = 1,
            [ProductType] = 1,
            [SpeedCharge] = 0,
            [ProductCharge] = 0;

        ALTER TABLE Parcels.Parcel ALTER COLUMN [Speed] TINYINT NOT NULL;
        ALTER TABLE Parcels.Parcel ALTER COLUMN [ProductType] TINYINT NOT NULL;
        ALTER TABLE Parcels.Parcel ALTER COLUMN [SpeedCharge] DECIMAL (10, 2) NOT NULL;
        ALTER TABLE Parcels.Parcel ALTER COLUMN [ProductCharge] DECIMAL (10, 2) NOT NULL;');
END;

IF OBJECT_ID(N'Delivery.PickupRequest', N'U') IS NOT NULL AND
   COL_LENGTH(N'Delivery.PickupRequest', N'ExpectedParcels') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'Delivery.chk_PickupRequest_Parcels', N'C') IS NOT NULL
    BEGIN
        ALTER TABLE Delivery.PickupRequest DROP CONSTRAINT [chk_PickupRequest_Parcels];
    END;

    ALTER TABLE Delivery.PickupRequest DROP COLUMN [ExpectedParcels], [Note];
END;
