-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Merchants.PickupPoint
-- Purpose: Where a rider collects a merchant's parcels. A parcel names one, or gets the merchant's default
--          (IsDefault = 1). Its area decides the hub that collects and, with the destination, the service area.
--          Latitude and Longitude are the door pinned on a map, both or neither, so the pickup van is guided to it.
-- Author: Courier team
-- Date: 2026-10-04
--       2026-10-13 Latitude, Longitude (the door on the map, for the rider's route)
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Merchants].[PickupPoint] (
    [Id]           BIGINT         IDENTITY (1, 1) NOT NULL,
    [TenantId]     BIGINT         NOT NULL,
    [MerchantId]   BIGINT         NOT NULL,
    [AreaId]       BIGINT         NOT NULL,
    [Name]         NVARCHAR (200) NOT NULL,
    [Address]      NVARCHAR (500) NOT NULL,
    [ContactPhone] NVARCHAR (20)  NOT NULL,
    [IsDefault]    BIT            DEFAULT ((0)) NOT NULL,
    [Latitude]     DECIMAL (9, 6) NULL,
    [Longitude]    DECIMAL (9, 6) NULL,
    [Archived]     BIT            DEFAULT ((0)) NOT NULL,
    [UpdatedId]    BIGINT         NULL,
    [UpdatedOn]    DATETIME2 (7)  DEFAULT (getutcdate()) NOT NULL,
    [Created]      DATETIME2 (0)  DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_PickupPoint_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_PickupPoint_Merchant] FOREIGN KEY ([MerchantId]) REFERENCES [Merchants].[Merchant] ([Id]),
    CONSTRAINT [FK_PickupPoint_Area] FOREIGN KEY ([AreaId]) REFERENCES [Network].[Area] ([Id]),
    CONSTRAINT [FK_PickupPoint_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_PickupPoint_Pin] CHECK (([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180))
);


GO
CREATE NONCLUSTERED INDEX [IX_PickupPoint_Tenant_Merchant]
    ON [Merchants].[PickupPoint]([TenantId] ASC, [MerchantId] ASC);


GO
-- One default pickup point per merchant
CREATE UNIQUE NONCLUSTERED INDEX [UX_PickupPoint_Merchant_Default]
    ON [Merchants].[PickupPoint]([MerchantId] ASC) WHERE ([IsDefault] = (1) AND [Archived] = (0));
