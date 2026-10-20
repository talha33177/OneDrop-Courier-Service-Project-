-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Payments.GatewayStore
-- Purpose: The SSLCommerz stores a courier takes online payments with, set by its super admin on /Admin/Connections.
--          At most one per courier (UX_GatewayStore_Tenant); with none, the app uses the server's own Sslcommerz:*
--          settings. Passwords are encrypted by the app (ASP.NET Data Protection, key ring in
--          Platform.DataProtectionKey) and never shown back
--          2026-10-20: keeps a sandbox store and a live store side by side (each an ID and a password, or neither),
--          and IsLive says which one takes the payments; StoreId and StorePassword were moved into them by
--          Scripts/Pre/006_TwoGatewayStores.sql
-- Author: Courier team
-- Date: 2026-10-18
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Payments].[GatewayStore] (
    [Id]              BIGINT          IDENTITY (1, 1) NOT NULL,
    [TenantId]        BIGINT          NOT NULL,
    [SandboxStoreId]  NVARCHAR (100)  NULL,
    [SandboxPassword] NVARCHAR (2000) NULL,
    [LiveStoreId]     NVARCHAR (100)  NULL,
    [LivePassword]    NVARCHAR (2000) NULL,
    [IsLive]          BIT             DEFAULT ((0)) NOT NULL,
    [UpdatedId]       BIGINT          NULL,
    [UpdatedOn]       DATETIME2 (7)   DEFAULT (getutcdate()) NOT NULL,
    [Created]         DATETIME2 (0)   DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_GatewayStore_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_GatewayStore_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_GatewayStore_Sandbox] CHECK (([SandboxStoreId] IS NULL AND [SandboxPassword] IS NULL) OR ([SandboxStoreId] IS NOT NULL AND [SandboxPassword] IS NOT NULL)),
    CONSTRAINT [chk_GatewayStore_Live] CHECK (([LiveStoreId] IS NULL AND [LivePassword] IS NULL) OR ([LiveStoreId] IS NOT NULL AND [LivePassword] IS NOT NULL)),
    CONSTRAINT [chk_GatewayStore_InUse] CHECK (([IsLive] = 1 AND [LiveStoreId] IS NOT NULL) OR ([IsLive] = 0 AND [SandboxStoreId] IS NOT NULL))
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_GatewayStore_Tenant]
    ON [Payments].[GatewayStore]([TenantId] ASC);
