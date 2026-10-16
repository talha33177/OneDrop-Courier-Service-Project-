-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 009_PickupsOnlyForPickupVans.sql
-- Purpose: Only a pickup van (Delivery.Rider.Vehicle 3) collects from merchants; bicycles and motorbikes take parcels to
--          recipients. A pickup that was given to a rider on anything else, and not yet collected, goes back on its hub's
--          list (Scheduled, no rider) for the hub to give to a pickup van.
-- Author: Courier team
-- Date: 2026-10-13
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
UPDATE Pickup
SET
    RiderId = NULL,
    Status = 1,
    UpdatedOn = GETUTCDATE()
FROM
    Delivery.PickupRequest Pickup
    INNER JOIN
        Delivery.Rider Rider
    ON
        Rider.Id = Pickup.RiderId
WHERE
    Pickup.Status = 2 AND
    Rider.Vehicle <> 3;
