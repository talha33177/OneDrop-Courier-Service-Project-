-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Delivery.BagParcel
-- Purpose: One parcel in a Delivery.Bag: ReceivedOn when a hub scanned it in after the bag was sealed, Missing when the
--          bag was closed without it (the parcel was flagged exceptional). A parcel is in many bags over its life, at
--          most once in each.
-- Author: Courier team
-- Date: 2026-10-21
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Delivery].[BagParcel] (
    [Id]         BIGINT        IDENTITY (1, 1) NOT NULL,
    [TenantId]   BIGINT        NOT NULL,
    [BagId]      BIGINT        NOT NULL,
    [ParcelId]   BIGINT        NOT NULL,
    [ReceivedOn] DATETIME2 (7) NULL,
    [Missing]    BIT           DEFAULT ((0)) NOT NULL,
    [UpdatedId]  BIGINT        NULL,
    [UpdatedOn]  DATETIME2 (7) DEFAULT (getutcdate()) NOT NULL,
    [Created]    DATETIME2 (0) DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_BagParcel_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_BagParcel_Bag] FOREIGN KEY ([BagId]) REFERENCES [Delivery].[Bag] ([Id]),
    CONSTRAINT [FK_BagParcel_Parcel] FOREIGN KEY ([ParcelId]) REFERENCES [Parcels].[Parcel] ([Id]),
    CONSTRAINT [FK_BagParcel_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_BagParcel_Missing] CHECK ([Missing] = 0 OR [ReceivedOn] IS NULL)
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_BagParcel_Bag_Parcel]
    ON [Delivery].[BagParcel]([BagId] ASC, [ParcelId] ASC);


GO
CREATE NONCLUSTERED INDEX [IX_BagParcel_Tenant_Parcel]
    ON [Delivery].[BagParcel]([TenantId] ASC, [ParcelId] ASC);
