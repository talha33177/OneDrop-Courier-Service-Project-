-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 008_DeliveryOptions.sql
-- Purpose: The delivery speeds and kinds of contents a courier starts with, now rows its admin changes on the
--          Delivery types page. Every courier gets the two defaults (regular, general goods: everywhere at no extra).
--          OneDrop Courier also gets what it sold before (owner, 2026-10-09): express ৳40, delivered the day after
--          pickup, inside the city and to a suburb; liquid ৳10 and fragile ৳20 everywhere; fresh food ৳30 and frozen
--          ৳50 inside the city and to a suburb only. Express and food that spoils go first. On a database that
--          already had them (Scripts/Pre/004_DeliveryOptions.sql copied them from the rate card) this adds nothing.
-- Author: Courier team
-- Date: 2026-10-13
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
DECLARE @Courier BIGINT = (SELECT Id FROM Platform.Tenant WHERE Slug = N'onedrop');

INSERT INTO Pricing.DeliveryOption (TenantId, Kind, Code, [Name], [Description], Handling, Icon, GoesFirst, IsDefault, SortOrder)
SELECT
    T.Id, O.Kind, O.Code, O.[Name], O.[Description], NULL, O.Icon, 0, 1, 1
FROM
    Platform.Tenant T
    CROSS JOIN (VALUES
        (1, N'regular', N'Regular', N'The usual delivery time', N'truck'),
        (2, N'general', N'General', N'Clothes, books, boxed goods', N'package')) O (Kind, Code, [Name], [Description], Icon)
WHERE
    NOT EXISTS (SELECT 1 FROM Pricing.DeliveryOption D WHERE D.TenantId = T.Id AND D.Kind = O.Kind AND D.IsDefault = 1);

IF @Courier IS NOT NULL AND
   NOT EXISTS (SELECT 1 FROM Pricing.DeliveryOption WHERE TenantId = @Courier AND IsDefault = 0)
BEGIN
    INSERT INTO Pricing.DeliveryOption (TenantId, Kind, Code, [Name], [Description], Handling, Icon, GoesFirst, IsDefault, SortOrder)
    VALUES
        (@Courier, 1, N'express', N'Express', N'Sooner, and first on the rider''s run', NULL, N'zap', 1, 0, 2),
        (@Courier, 2, N'liquid', N'Liquid', N'Bottles: sealed and kept upright', N'Liquid: keep upright', N'droplet', 0, 0, 2),
        (@Courier, 2, N'fragile', N'Fragile', N'Glass, ceramics, screens', N'Fragile: handle with care', N'glass', 0, 0, 3),
        (@Courier, 2, N'fresh-food', N'Fresh food', N'Cakes, fruit, cooked food', N'Fresh food: deliver first', N'apple', 1, 0, 4),
        (@Courier, 2, N'frozen', N'Frozen', N'Carried in a cold box', N'Frozen: keep in the cold box', N'snowflake', 1, 0, 5);

    INSERT INTO Pricing.DeliveryOptionCharge (TenantId, DeliveryOptionId, ServiceArea, Charge, DeliveryDays)
    SELECT
        @Courier, D.Id, C.ServiceArea, C.Charge, C.DeliveryDays
    FROM
        (VALUES
            (N'express', 1, 40, 1), (N'express', 2, 40, 1),
            (N'liquid', 1, 10, NULL), (N'liquid', 2, 10, NULL), (N'liquid', 3, 10, NULL),
            (N'fragile', 1, 20, NULL), (N'fragile', 2, 20, NULL), (N'fragile', 3, 20, NULL),
            (N'fresh-food', 1, 30, NULL), (N'fresh-food', 2, 30, NULL),
            (N'frozen', 1, 50, NULL), (N'frozen', 2, 50, NULL)) C (Code, ServiceArea, Charge, DeliveryDays)
        INNER JOIN
        Pricing.DeliveryOption D
        ON D.TenantId = @Courier AND D.Code = C.Code;
END;
