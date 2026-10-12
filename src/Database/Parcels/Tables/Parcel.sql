-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Parcels.Parcel
-- Purpose: One consignment from a merchant to a recipient. Status is a TINYINT enum (Domain.Parcels.ParcelStatus):
--          1 Pending, 2 PickedUp, 3 AtHub, 4 InTransit, 5 OutForDelivery, 6 OnHold, 7 Delivered, 8 PartlyDelivered,
--          9 Returning, 10 Returned, 11 Cancelled. Where it is: CurrentHubId at a hub, TransferToHubId between hubs,
--          RiderId with a rider; at most one is set. PickupHubId collects it and takes returns back, DeliveryHubId
--          delivers it. ServiceArea (1 inside city, 2 suburb, 3 outside city), DeliveryCharge, CodChargePercent and
--          ReturnCharge are the rate it was booked at, DeliveryDays the days after pickup it was promised in (NULL: no
--          promise). IncludedWeightGrams, BaseCharge and ExtraKgCharge are the weight rule it was booked under, kept
--          so a hub's reweigh prices it as it was sold; MeasuredWeightGrams is what the hub's scale said and, when it
--          is set, the weight DeliveryCharge follows. Speed (Domain.Pricing.DeliverySpeed: 1 regular, 2 express) and
--          ProductType (Domain.Pricing.ProductType: 1 general, 2 liquid, 3 fragile, 4 fresh food, 5 frozen) are how it
--          goes and what is inside; SpeedCharge and ProductCharge are their extras as booked, both part of
--          DeliveryCharge. RecipientLatitude and RecipientLongitude are the door pinned on a map, both or neither.
--          DueOn is the day it should be delivered by, set when the courier
--          takes it and moved to a later day the recipient asks for. Issue is a problem a hub flagged for the courier
--          to look at (Domain.Parcels.ParcelIssue: 1 in review, 2 exceptional) with IssueNote and IssueRaisedOn, the
--          time it was first flagged; all three are NULL while there is none, and a flagged parcel is not handed to a
--          rider. A merchant's retry with the same Idempotency-Key returns the first parcel; RequestHash detects the
--          key reused for another body. RowVersion guards concurrent changes (hub scan versus rider app). The four
--          keys to Network.Hub are named FK_Parcel_Hub_{Column}
-- Author: Courier team
-- Date: 2026-10-04
--       2026-10-05 DeliveryDays, DueOn
--       2026-10-05 MeasuredWeightGrams, IncludedWeightGrams, BaseCharge, ExtraKgCharge (reweighing at the hub)
--       2026-10-07 Issue, IssueNote, IssueRaisedOn (problems a hub flags)
--       2026-10-09 Speed, ProductType, SpeedCharge, ProductCharge (delivery types), RecipientLatitude and
--                  RecipientLongitude (the door on a map)
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Parcels].[Parcel] (
    [Id]                  BIGINT          IDENTITY (1, 1) NOT NULL,
    [TenantId]            BIGINT          NOT NULL,
    [MerchantId]          BIGINT          NOT NULL,
    [PickupPointId]       BIGINT          NOT NULL,
    [PickupHubId]         BIGINT          NOT NULL,
    [AreaId]              BIGINT          NOT NULL,
    [DeliveryHubId]       BIGINT          NOT NULL,
    [TrackingCode]        NVARCHAR (20)   CONSTRAINT [DF_Parcel_TrackingCode] DEFAULT (concat(N'OD', NEXT VALUE FOR [Parcels].[TrackingNumber])) NOT NULL,
    [MerchantReference]   NVARCHAR (100)  NULL,
    [IdempotencyKey]      NVARCHAR (100)  NULL,
    [RequestHash]         BINARY (32)     NULL,
    [RecipientName]       NVARCHAR (200)  NOT NULL,
    [RecipientPhone]      NVARCHAR (20)   NOT NULL,
    [RecipientAddress]    NVARCHAR (500)  NOT NULL,
    [RecipientLatitude]   DECIMAL (9, 6)  NULL,
    [RecipientLongitude]  DECIMAL (9, 6)  NULL,
    [ItemDescription]     NVARCHAR (200)  NULL,
    [WeightGrams]         INT             NOT NULL,
    [MeasuredWeightGrams] INT             NULL,
    [CodAmount]           DECIMAL (12, 2) NOT NULL,
    [Note]                NVARCHAR (500)  NULL,
    [ServiceArea]         TINYINT         NOT NULL,
    [DeliveryCharge]      DECIMAL (10, 2) NOT NULL,
    [CodChargePercent]    DECIMAL (5, 2)  NOT NULL,
    [ReturnCharge]        DECIMAL (10, 2) NOT NULL,
    [IncludedWeightGrams] INT             NOT NULL,
    [BaseCharge]          DECIMAL (10, 2) NOT NULL,
    [ExtraKgCharge]       DECIMAL (10, 2) NOT NULL,
    [Speed]               TINYINT         NOT NULL,
    [ProductType]         TINYINT         NOT NULL,
    [SpeedCharge]         DECIMAL (10, 2) NOT NULL,
    [ProductCharge]       DECIMAL (10, 2) NOT NULL,
    [DeliveryDays]        INT             NULL,
    [DueOn]               DATE            NULL,
    [Status]              TINYINT         NOT NULL,
    [CurrentHubId]        BIGINT          NULL,
    [TransferToHubId]     BIGINT          NULL,
    [RiderId]             BIGINT          NULL,
    [Attempts]            INT             DEFAULT ((0)) NOT NULL,
    [HoldReason]          NVARCHAR (200)  NULL,
    [HoldUntil]           DATE            NULL,
    [ReturnReason]        NVARCHAR (200)  NULL,
    [Issue]               TINYINT         NULL,
    [IssueNote]           NVARCHAR (200)  NULL,
    [IssueRaisedOn]       DATETIME2 (7)   NULL,
    [CollectedAmount]     DECIMAL (12, 2) NULL,
    [CodCharge]           DECIMAL (10, 2) NULL,
    [ClosedOn]            DATETIME2 (7)   NULL,
    [RowVersion]          ROWVERSION      NOT NULL,
    [UpdatedId]           BIGINT          NULL,
    [UpdatedOn]           DATETIME2 (7)   DEFAULT (getutcdate()) NOT NULL,
    [Created]             DATETIME2 (0)   DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_Parcel_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_Parcel_Merchant] FOREIGN KEY ([MerchantId]) REFERENCES [Merchants].[Merchant] ([Id]),
    CONSTRAINT [FK_Parcel_PickupPoint] FOREIGN KEY ([PickupPointId]) REFERENCES [Merchants].[PickupPoint] ([Id]),
    CONSTRAINT [FK_Parcel_Area] FOREIGN KEY ([AreaId]) REFERENCES [Network].[Area] ([Id]),
    CONSTRAINT [FK_Parcel_Hub_PickupHubId] FOREIGN KEY ([PickupHubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_Parcel_Hub_DeliveryHubId] FOREIGN KEY ([DeliveryHubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_Parcel_Hub_CurrentHubId] FOREIGN KEY ([CurrentHubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_Parcel_Hub_TransferToHubId] FOREIGN KEY ([TransferToHubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_Parcel_Rider] FOREIGN KEY ([RiderId]) REFERENCES [Delivery].[Rider] ([Id]),
    CONSTRAINT [FK_Parcel_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_Parcel_Status] CHECK ([Status] BETWEEN 1 AND 11),
    CONSTRAINT [chk_Parcel_ServiceArea] CHECK ([ServiceArea] BETWEEN 1 AND 3),
    CONSTRAINT [chk_Parcel_Speed] CHECK ([Speed] BETWEEN 1 AND 2),
    CONSTRAINT [chk_Parcel_ProductType] CHECK ([ProductType] BETWEEN 1 AND 5),
    CONSTRAINT [chk_Parcel_Extras] CHECK ([SpeedCharge] >= (0) AND [ProductCharge] >= (0)),
    CONSTRAINT [chk_Parcel_Pin] CHECK (([RecipientLatitude] IS NULL AND [RecipientLongitude] IS NULL) OR ([RecipientLatitude] BETWEEN -90 AND 90 AND [RecipientLongitude] BETWEEN -180 AND 180)),
    CONSTRAINT [chk_Parcel_Weight] CHECK ([WeightGrams] > (0)),
    CONSTRAINT [chk_Parcel_Amounts] CHECK ([CodAmount] >= (0) AND [DeliveryCharge] >= (0) AND [CodChargePercent] >= (0) AND [ReturnCharge] >= (0) AND [Attempts] >= (0)),
    CONSTRAINT [chk_Parcel_Location] CHECK ((CASE WHEN [CurrentHubId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [TransferToHubId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [RiderId] IS NULL THEN 0 ELSE 1 END) <= 1),
    CONSTRAINT [chk_Parcel_Issue] CHECK (([Issue] IS NULL AND [IssueNote] IS NULL AND [IssueRaisedOn] IS NULL) OR ([Issue] BETWEEN 1 AND 2 AND [IssueNote] IS NOT NULL AND [IssueRaisedOn] IS NOT NULL))
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Parcel_TrackingCode]
    ON [Parcels].[Parcel]([TrackingCode] ASC);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Parcel_Merchant_IdempotencyKey]
    ON [Parcels].[Parcel]([MerchantId] ASC, [IdempotencyKey] ASC) WHERE ([IdempotencyKey] IS NOT NULL);


GO
CREATE NONCLUSTERED INDEX [IX_Parcel_Tenant_Merchant_Created]
    ON [Parcels].[Parcel]([TenantId] ASC, [MerchantId] ASC, [Created] DESC)
    INCLUDE([Status]);


GO
CREATE NONCLUSTERED INDEX [IX_Parcel_Tenant_Status]
    ON [Parcels].[Parcel]([TenantId] ASC, [Status] ASC);


GO
-- The fraud check counts a phone's parcels across every merchant of the tenant
CREATE NONCLUSTERED INDEX [IX_Parcel_Tenant_RecipientPhone]
    ON [Parcels].[Parcel]([TenantId] ASC, [RecipientPhone] ASC)
    INCLUDE([Status]);


GO
CREATE NONCLUSTERED INDEX [IX_Parcel_PickupPoint_Status]
    ON [Parcels].[Parcel]([PickupPointId] ASC, [Status] ASC);


GO
CREATE NONCLUSTERED INDEX [IX_Parcel_CurrentHubId]
    ON [Parcels].[Parcel]([CurrentHubId] ASC) WHERE ([CurrentHubId] IS NOT NULL);


GO
CREATE NONCLUSTERED INDEX [IX_Parcel_TransferToHubId]
    ON [Parcels].[Parcel]([TransferToHubId] ASC) WHERE ([TransferToHubId] IS NOT NULL);


GO
CREATE NONCLUSTERED INDEX [IX_Parcel_RiderId]
    ON [Parcels].[Parcel]([RiderId] ASC) WHERE ([RiderId] IS NOT NULL);


GO
CREATE NONCLUSTERED INDEX [IX_Parcel_AreaId]
    ON [Parcels].[Parcel]([AreaId] ASC);


GO
-- Flagged parcels: the in review and exceptional tabs, and the courier's list of problems to look at
CREATE NONCLUSTERED INDEX [IX_Parcel_Tenant_Issue]
    ON [Parcels].[Parcel]([TenantId] ASC, [Issue] ASC)
    INCLUDE([MerchantId]) WHERE ([Issue] IS NOT NULL);


GO
-- Late parcels: due before today and still on their way
CREATE NONCLUSTERED INDEX [IX_Parcel_Tenant_DueOn]
    ON [Parcels].[Parcel]([TenantId] ASC, [DueOn] ASC)
    INCLUDE([Status]) WHERE ([DueOn] IS NOT NULL);
