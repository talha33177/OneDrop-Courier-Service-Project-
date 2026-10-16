-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Pricing.DeliveryOptionCharge
-- Purpose: What a Pricing.DeliveryOption adds to the delivery charge in one service area (TINYINT enum
--          Domain.Pricing.ServiceArea: 1 inside city, 2 suburb, 3 outside city), and for a speed the days after
--          pickup it is delivered in there (NULL: the rate card's time). An option with no row for a service area
--          is not offered there; the admin removes the row to stop offering it. A parcel keeps the extra it was
--          booked at (Parcels.Parcel.SpeedCharge, ProductCharge), so a row changes only future bookings
-- Author: Courier team
-- Date: 2026-10-13
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
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


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_DeliveryOptionCharge_Tenant_Option_ServiceArea]
    ON [Pricing].[DeliveryOptionCharge]([TenantId] ASC, [DeliveryOptionId] ASC, [ServiceArea] ASC);
