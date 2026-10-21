-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Delivery.Bag
-- Purpose: A bag of parcels one hub (FromHubId) sends to another (ToHubId) on the line-haul truck; its parcels are
--          Delivery.BagParcel rows. Status (TINYINT enum Domain.Delivery.BagStatus): 1 Open (being filled at FromHubId),
--          2 Sealed (on the truck, Vehicle, by SealedById at SealedOn; its parcels left the hub), 3 Arrived (ToHubId
--          scanned it or its first parcel, ArrivedOn), 4 Closed (every parcel scanned in, or ClosedById closed it and
--          the rest were flagged missing). One open bag per pair of hubs (UX_Bag_From_To_Open). Number (BG-100001)
--          comes from Delivery.BagNumber.
-- Author: Courier team
-- Date: 2026-10-21
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Delivery].[Bag] (
    [Id]         BIGINT         IDENTITY (1, 1) NOT NULL,
    [TenantId]   BIGINT         NOT NULL,
    [FromHubId]  BIGINT         NOT NULL,
    [ToHubId]    BIGINT         NOT NULL,
    [Number]     NVARCHAR (20)  CONSTRAINT [DF_Bag_Number] DEFAULT (concat(N'BG-', NEXT VALUE FOR [Delivery].[BagNumber])) NOT NULL,
    [Status]     TINYINT        NOT NULL,
    [Vehicle]    NVARCHAR (100) NULL,
    [SealedOn]   DATETIME2 (7)  NULL,
    [SealedById] BIGINT         NULL,
    [ArrivedOn]  DATETIME2 (7)  NULL,
    [ClosedOn]   DATETIME2 (7)  NULL,
    [ClosedById] BIGINT         NULL,
    [RowVersion] ROWVERSION     NOT NULL,
    [UpdatedId]  BIGINT         NULL,
    [UpdatedOn]  DATETIME2 (7)  DEFAULT (getutcdate()) NOT NULL,
    [Created]    DATETIME2 (0)  DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_Bag_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_Bag_Hub_FromHubId] FOREIGN KEY ([FromHubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_Bag_Hub_ToHubId] FOREIGN KEY ([ToHubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_Bag_User_SealedById] FOREIGN KEY ([SealedById]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [FK_Bag_User_ClosedById] FOREIGN KEY ([ClosedById]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [FK_Bag_User_UpdatedId] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_Bag_Hubs] CHECK ([FromHubId] <> [ToHubId]),
    CONSTRAINT [chk_Bag_Status] CHECK ([Status] BETWEEN 1 AND 4),
    CONSTRAINT [chk_Bag_Sealed] CHECK (([Status] = 1 AND [SealedOn] IS NULL) OR ([Status] > 1 AND [SealedOn] IS NOT NULL)),
    CONSTRAINT [chk_Bag_Closed] CHECK (([Status] = 4 AND [ClosedOn] IS NOT NULL) OR ([Status] < 4 AND [ClosedOn] IS NULL AND [ClosedById] IS NULL))
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Bag_Number]
    ON [Delivery].[Bag]([Number] ASC);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Bag_From_To_Open]
    ON [Delivery].[Bag]([TenantId] ASC, [FromHubId] ASC, [ToHubId] ASC) WHERE [Status] = 1;


GO
CREATE NONCLUSTERED INDEX [IX_Bag_Tenant_From_Status]
    ON [Delivery].[Bag]([TenantId] ASC, [FromHubId] ASC, [Status] ASC);


GO
CREATE NONCLUSTERED INDEX [IX_Bag_Tenant_To_Status]
    ON [Delivery].[Bag]([TenantId] ASC, [ToHubId] ASC, [Status] ASC);
