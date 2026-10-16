-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 004_DeliveryOptions.sql
-- Purpose: Delivery speeds and kinds of contents become rows the courier's admin makes (Pricing.DeliveryOption and
--          Pricing.DeliveryOptionCharge) instead of fixed numbers, which SqlPackage cannot do to tables with rows:
--          Parcels.Parcel.Speed and ProductType (TINYINT) become SpeedId and ProductTypeId (NOT NULL keys to the new
--          table), and Pricing.DeliveryRate loses ExpressCharge, ExpressDays, LiquidCharge, FragileCharge,
--          FreshFoodCharge and FrozenCharge. So this script makes the two tables as the publish will (the publish adds
--          their foreign keys from the parcel and their lines in the security policy), gives every courier the seven
--          options it had (regular and general goods the defaults), copies each rate's extras and express time
--          across, points every parcel at its options and drops the old columns. Runs before the publish, against the
--          old schema; does nothing on a new database or once it is done. A new database gets its options from
--          Scripts/2026/008_DeliveryOptions.sql.
-- Author: Courier team
-- Date: 2026-10-13
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
IF OBJECT_ID(N'Parcels.Parcel', N'U') IS NOT NULL AND
   COL_LENGTH(N'Parcels.Parcel', N'Speed') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'Pricing.DeliveryOption', N'U') IS NULL
    BEGIN
        CREATE TABLE [Pricing].[DeliveryOption] (
            [Id]          BIGINT         IDENTITY (1, 1) NOT NULL,
            [TenantId]    BIGINT         NOT NULL,
            [Kind]        TINYINT        NOT NULL,
            [Code]        NVARCHAR (30)  NOT NULL,
            [Name]        NVARCHAR (40)  NOT NULL,
            [Description] NVARCHAR (120) NULL,
            [Handling]    NVARCHAR (80)  NULL,
            [Icon]        NVARCHAR (30)  NOT NULL,
            [GoesFirst]   BIT            DEFAULT ((0)) NOT NULL,
            [IsDefault]   BIT            DEFAULT ((0)) NOT NULL,
            [SortOrder]   INT            DEFAULT ((0)) NOT NULL,
            [Archived]    BIT            DEFAULT ((0)) NOT NULL,
            [UpdatedId]   BIGINT         NULL,
            [UpdatedOn]   DATETIME2 (7)  DEFAULT (getutcdate()) NOT NULL,
            [Created]     DATETIME2 (0)  DEFAULT (getutcdate()) NOT NULL,
            PRIMARY KEY CLUSTERED ([Id] ASC),
            CONSTRAINT [FK_DeliveryOption_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
            CONSTRAINT [FK_DeliveryOption_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
            CONSTRAINT [chk_DeliveryOption_Kind] CHECK ([Kind] BETWEEN 1 AND 2),
            CONSTRAINT [chk_DeliveryOption_Default] CHECK ([IsDefault] = 0 OR [Archived] = 0)
        );

        CREATE UNIQUE NONCLUSTERED INDEX [UX_DeliveryOption_Tenant_Kind_Code]
            ON [Pricing].[DeliveryOption]([TenantId] ASC, [Kind] ASC, [Code] ASC);

        CREATE UNIQUE NONCLUSTERED INDEX [UX_DeliveryOption_Tenant_Kind_Default]
            ON [Pricing].[DeliveryOption]([TenantId] ASC, [Kind] ASC) WHERE ([IsDefault] = 1);
    END;

    IF OBJECT_ID(N'Pricing.DeliveryOptionCharge', N'U') IS NULL
    BEGIN
        CREATE TABLE [Pricing].[DeliveryOptionCharge] (
            [Id]               BIGINT          IDENTITY (1, 1) NOT NULL,
            [TenantId]         BIGINT          NOT NULL,
            [DeliveryOptionId] BIGINT          NOT NULL,
            [ServiceArea]      TINYINT         NOT NULL,
            [Charge]           DECIMAL (10, 2) NOT NULL,
            [DeliveryDays]     INT             NULL,
            [UpdatedId]        BIGINT          NULL,
            [UpdatedOn]        DATETIME2 (7)   DEFAULT (getutcdate()) NOT NULL,
            [Created]          DATETIME2 (0)   DEFAULT (getutcdate()) NOT NULL,
            PRIMARY KEY CLUSTERED ([Id] ASC),
            CONSTRAINT [FK_DeliveryOptionCharge_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
            CONSTRAINT [FK_DeliveryOptionCharge_DeliveryOption] FOREIGN KEY ([DeliveryOptionId]) REFERENCES [Pricing].[DeliveryOption] ([Id]),
            CONSTRAINT [FK_DeliveryOptionCharge_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
            CONSTRAINT [chk_DeliveryOptionCharge_ServiceArea] CHECK ([ServiceArea] BETWEEN 1 AND 3),
            CONSTRAINT [chk_DeliveryOptionCharge_Charge] CHECK ([Charge] >= (0)),
            CONSTRAINT [chk_DeliveryOptionCharge_Days] CHECK ([DeliveryDays] IS NULL OR [DeliveryDays] BETWEEN 0 AND 30)
        );

        CREATE UNIQUE NONCLUSTERED INDEX [UX_DeliveryOptionCharge_Tenant_Option_ServiceArea]
            ON [Pricing].[DeliveryOptionCharge]([TenantId] ASC, [DeliveryOptionId] ASC, [ServiceArea] ASC);
    END;

    -- The old columns and the new tables are not known when this batch is compiled, so the rest runs as batches of
    -- their own
    EXEC (N'
        DECLARE @Options TABLE (
            Kind TINYINT, Code NVARCHAR (30), [Name] NVARCHAR (40), [Description] NVARCHAR (120),
            Handling NVARCHAR (80), Icon NVARCHAR (30), GoesFirst BIT, IsDefault BIT, SortOrder INT);

        INSERT INTO @Options
        VALUES
            (1, N''regular'', N''Regular'', N''The usual delivery time'', NULL, N''truck'', 0, 1, 1),
            (1, N''express'', N''Express'', N''Sooner, and first on the rider''''s run'', NULL, N''zap'', 1, 0, 2),
            (2, N''general'', N''General'', N''Clothes, books, boxed goods'', NULL, N''package'', 0, 1, 1),
            (2, N''liquid'', N''Liquid'', N''Bottles: sealed and kept upright'', N''Liquid: keep upright'', N''droplet'', 0, 0, 2),
            (2, N''fragile'', N''Fragile'', N''Glass, ceramics, screens'', N''Fragile: handle with care'', N''glass'', 0, 0, 3),
            (2, N''fresh-food'', N''Fresh food'', N''Cakes, fruit, cooked food'', N''Fresh food: deliver first'', N''apple'', 1, 0, 4),
            (2, N''frozen'', N''Frozen'', N''Carried in a cold box'', N''Frozen: keep in the cold box'', N''snowflake'', 1, 0, 5);

        INSERT INTO Pricing.DeliveryOption (TenantId, Kind, Code, [Name], [Description], Handling, Icon, GoesFirst, IsDefault, SortOrder)
        SELECT
            T.Id, O.Kind, O.Code, O.[Name], O.[Description], O.Handling, O.Icon, O.GoesFirst, O.IsDefault, O.SortOrder
        FROM
            Platform.Tenant T
            CROSS JOIN @Options O
        WHERE
            NOT EXISTS (SELECT 1 FROM Pricing.DeliveryOption D WHERE D.TenantId = T.Id AND D.Kind = O.Kind AND D.Code = O.Code);

        INSERT INTO Pricing.DeliveryOptionCharge (TenantId, DeliveryOptionId, ServiceArea, Charge, DeliveryDays)
        SELECT
            R.TenantId, D.Id, R.ServiceArea, E.Charge, E.DeliveryDays
        FROM
            Pricing.DeliveryRate R
            CROSS APPLY (VALUES
                (1, N''express'', R.ExpressCharge, R.ExpressDays),
                (2, N''liquid'', R.LiquidCharge, NULL),
                (2, N''fragile'', R.FragileCharge, NULL),
                (2, N''fresh-food'', R.FreshFoodCharge, NULL),
                (2, N''frozen'', R.FrozenCharge, NULL)) E (Kind, Code, Charge, DeliveryDays)
            INNER JOIN
            Pricing.DeliveryOption D
            ON D.TenantId = R.TenantId AND D.Kind = E.Kind AND D.Code = E.Code
        WHERE
            E.Charge IS NOT NULL AND
            NOT EXISTS (SELECT 1 FROM Pricing.DeliveryOptionCharge C WHERE C.DeliveryOptionId = D.Id AND C.ServiceArea = R.ServiceArea);

        ALTER TABLE Parcels.Parcel ADD
            [SpeedId]       BIGINT NULL,
            [ProductTypeId] BIGINT NULL;');

    EXEC (N'
        DECLARE @Codes TABLE (Kind TINYINT, OldValue TINYINT, Code NVARCHAR (30));

        INSERT INTO @Codes
        VALUES
            (1, 1, N''regular''), (1, 2, N''express''),
            (2, 1, N''general''), (2, 2, N''liquid''), (2, 3, N''fragile''), (2, 4, N''fresh-food''), (2, 5, N''frozen'');

        UPDATE P
        SET
            SpeedId = S.Id,
            ProductTypeId = C.Id
        FROM
            Parcels.Parcel P
            INNER JOIN
            @Codes SC
            ON SC.Kind = 1 AND SC.OldValue = P.Speed
            INNER JOIN
            Pricing.DeliveryOption S
            ON S.TenantId = P.TenantId AND S.Kind = 1 AND S.Code = SC.Code
            INNER JOIN
            @Codes CC
            ON CC.Kind = 2 AND CC.OldValue = P.ProductType
            INNER JOIN
            Pricing.DeliveryOption C
            ON C.TenantId = P.TenantId AND C.Kind = 2 AND C.Code = CC.Code;

        ALTER TABLE Parcels.Parcel ALTER COLUMN [SpeedId] BIGINT NOT NULL;
        ALTER TABLE Parcels.Parcel ALTER COLUMN [ProductTypeId] BIGINT NOT NULL;

        IF OBJECT_ID(N''Parcels.chk_Parcel_Speed'', N''C'') IS NOT NULL
        BEGIN
            ALTER TABLE Parcels.Parcel DROP CONSTRAINT [chk_Parcel_Speed];
        END;

        IF OBJECT_ID(N''Parcels.chk_Parcel_ProductType'', N''C'') IS NOT NULL
        BEGIN
            ALTER TABLE Parcels.Parcel DROP CONSTRAINT [chk_Parcel_ProductType];
        END;

        ALTER TABLE Parcels.Parcel DROP COLUMN [Speed], [ProductType];

        IF OBJECT_ID(N''Pricing.chk_DeliveryRate_Extras'', N''C'') IS NOT NULL
        BEGIN
            ALTER TABLE Pricing.DeliveryRate DROP CONSTRAINT [chk_DeliveryRate_Extras];
        END;

        IF OBJECT_ID(N''Pricing.chk_DeliveryRate_ExpressDays'', N''C'') IS NOT NULL
        BEGIN
            ALTER TABLE Pricing.DeliveryRate DROP CONSTRAINT [chk_DeliveryRate_ExpressDays];
        END;

        ALTER TABLE Pricing.DeliveryRate DROP COLUMN
            [ExpressCharge], [ExpressDays], [LiquidCharge], [FragileCharge], [FreshFoodCharge], [FrozenCharge];');
END;
