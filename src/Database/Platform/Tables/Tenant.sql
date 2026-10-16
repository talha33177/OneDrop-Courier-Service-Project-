-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Platform.Tenant
-- Purpose: A courier company on the platform. Every other business table carries a TenantId pointing here. Selected
--          per request by Slug, the subdomain: onedrop.onedrop.app. Its rate card is Pricing.DeliveryRate. No setting
--          has a default: every tenant states its own; a NULL setting means the tenant sets no such rule.
-- Author: Courier team
-- Date: 2026-10-04
--       2026-10-05 RiderReturnTime
--       2026-10-06 MaxBusinessesPerAccount
--       2026-10-09 PickupCutoff, PickupTime
--       2026-10-12 StuckAfterDays
--       2026-10-13 PickupCutoff dropped (Scripts/Pre/005_DropPickupCutoff.sql): the truck takes what is booked until
--                  it reaches each pickup point, so there is no cut-off
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Platform].[Tenant] (
    [Id]                        BIGINT         IDENTITY (1, 1) NOT NULL,
    [Name]                      NVARCHAR (200) NOT NULL,
    [Slug]                      NVARCHAR (50)  NOT NULL,
    [TimeZone]                  NVARCHAR (100) NOT NULL,
    [CurrencyCode]              NCHAR (3)      NOT NULL,
    [SmsSenderName]             NVARCHAR (20)  NOT NULL,
    [SupportPhone]              NVARCHAR (20)  NOT NULL,
    -- Delivery attempts a parcel gets before it must be delivered or returned
    [MaxDeliveryAttempts]       INT            NOT NULL,
    -- The time of day (the tenant's clock) riders are due back at their hub with the day's cash; NULL for none
    [RiderReturnTime]           TIME (0)       NULL,
    -- Businesses a merchant account may add besides its main profile; NULL for no limit
    [MaxBusinessesPerAccount]   INT            NULL,
    -- When the day's pickup truck leaves the hub (the tenant's clock), as told to merchants; NULL for not said
    [PickupTime]                TIME (0)       NULL,
    -- Days a parcel may stay where it is (with a pickup rider, at a hub, between hubs, on hold past its day, going
    -- back) before the admin is told it is stuck; NULL for not watched
    [StuckAfterDays]            INT            NULL,
    [Archived]                  BIT            DEFAULT ((0)) NOT NULL,
    [UpdatedId]                 BIGINT         NULL,
    [UpdatedOn]                 DATETIME2 (7)  DEFAULT (getutcdate()) NOT NULL,
    [Created]                   DATETIME2 (0)  DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_Tenant_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_Tenant_MaxDeliveryAttempts] CHECK ([MaxDeliveryAttempts] BETWEEN 1 AND 10),
    CONSTRAINT [chk_Tenant_MaxBusinessesPerAccount] CHECK ([MaxBusinessesPerAccount] >= 1),
    CONSTRAINT [chk_Tenant_StuckAfterDays] CHECK ([StuckAfterDays] >= 1)
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Tenant_Slug]
    ON [Platform].[Tenant]([Slug] ASC);
