-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Platform.DataProtectionKey
-- Purpose: The app's ASP.NET Data Protection key ring, one XML key per row, written and read by the app alone
--          (Infrastructure.Security.SqlKeyRepository). It signs the sign-in cookies and encrypts the secrets a
--          courier keeps (Notifications.MailServer.Password, Payments.GatewayStore.StorePassword), so it lives in the
--          database to survive restarts and redeploys. The platform's, not a tenant's: no TenantId, not in the
--          security policy. Never edit or delete a row: what it encrypted could no longer be read
-- Author: Courier team
-- Date: 2026-10-18
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Platform].[DataProtectionKey] (
    [Id]           BIGINT         IDENTITY (1, 1) NOT NULL,
    [FriendlyName] NVARCHAR (100) NOT NULL,
    [Xml]          NVARCHAR (MAX) NOT NULL,
    [Created]      DATETIME2 (0)  DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC)
);
