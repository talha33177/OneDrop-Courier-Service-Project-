-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Payments.GatewayStore
-- Purpose: The SSLCommerz store a courier takes online payments with, set by its super admin on /Admin/Connections.
--          At most one per courier (UX_GatewayStore_Tenant); with none, the app uses the server's own Sslcommerz:*
--          settings. IsLive picks SSLCommerz's live gateway (real money) over its sandbox. StorePassword is encrypted
--          by the app (ASP.NET Data Protection, key ring in Platform.DataProtectionKey) and never shown back
-- Author: Courier team
-- Date: 2026-10-18
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Payments].[GatewayStore] (
    [Id]            BIGINT          IDENTITY (1, 1) NOT NULL,
    [TenantId]      BIGINT          NOT NULL,
    [StoreId]       NVARCHAR (100)  NOT NULL,
    [StorePassword] NVARCHAR (2000) NOT NULL,
    [IsLive]        BIT             DEFAULT ((0)) NOT NULL,
    [UpdatedId]     BIGINT          NULL,
    [UpdatedOn]     DATETIME2 (7)   DEFAULT (getutcdate()) NOT NULL,
    [Created]       DATETIME2 (0)   DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_GatewayStore_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_GatewayStore_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id])
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_GatewayStore_Tenant]
    ON [Payments].[GatewayStore]([TenantId] ASC);
