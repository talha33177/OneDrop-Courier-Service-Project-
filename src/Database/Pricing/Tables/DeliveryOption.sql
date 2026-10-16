-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Pricing.DeliveryOption
-- Purpose: A delivery speed or a kind of contents the courier sells, made and changed by its admin. Kind is a TINYINT
--          enum (Domain.Pricing.DeliveryOptionKind: 1 speed, 2 contents). Code is how the API and the CSV upload name
--          it, made from its first name and never changed. Description is the line under its name on the booking
--          form, Handling what labels, hubs and riders are told (contents only), Icon the name of its icon. GoesFirst
--          sorts its parcels first at the hub and on the rider's run. One option of each kind IsDefault: what a
--          merchant gets when choosing nothing, offered everywhere at no extra and never archived. Its extra per
--          service area is in Pricing.DeliveryOptionCharge. An option no longer sold is archived; its parcels keep it
-- Author: Courier team
-- Date: 2026-10-13
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
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


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_DeliveryOption_Tenant_Kind_Code]
    ON [Pricing].[DeliveryOption]([TenantId] ASC, [Kind] ASC, [Code] ASC);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_DeliveryOption_Tenant_Kind_Default]
    ON [Pricing].[DeliveryOption]([TenantId] ASC, [Kind] ASC) WHERE ([IsDefault] = 1);
