-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Notifications.MailServer
-- Purpose: The SMTP server a courier's emails go out through, set by its super admin on /Admin/Connections. At most
--          one per courier (UX_MailServer_Tenant); with none, the app uses the server's own Email:* settings.
--          Password is encrypted by the app (ASP.NET Data Protection, key ring in Platform.DataProtectionKey) and
--          never shown back; empty means the server takes mail without a login. IsEnabled off keeps the details but
--          sends nothing through them (and does not fall back to the server's settings: a saved server is the
--          courier's choice, on or off). UseSsl is a secure connection (STARTTLS, or SSL on port 465);
--          AllowSelfSigned accepts a certificate nobody vouches for, for a mail server of the courier's own
-- Author: Courier team
-- Date: 2026-10-18
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Notifications].[MailServer] (
    [Id]              BIGINT          IDENTITY (1, 1) NOT NULL,
    [TenantId]        BIGINT          NOT NULL,
    [Host]            NVARCHAR (200)  NOT NULL,
    [Port]            INT             NOT NULL,
    [UserName]        NVARCHAR (254)  NULL,
    [Password]        NVARCHAR (2000) NOT NULL,
    [FromAddress]     NVARCHAR (254)  NOT NULL,
    [FromName]        NVARCHAR (200)  NULL,
    [IsEnabled]       BIT             DEFAULT ((1)) NOT NULL,
    [UseSsl]          BIT             DEFAULT ((1)) NOT NULL,
    [AllowSelfSigned] BIT             DEFAULT ((0)) NOT NULL,
    [UpdatedId]       BIGINT          NULL,
    [UpdatedOn]       DATETIME2 (7)   DEFAULT (getutcdate()) NOT NULL,
    [Created]         DATETIME2 (0)   DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_MailServer_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_MailServer_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_MailServer_Port] CHECK ([Port] BETWEEN 1 AND 65535)
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_MailServer_Tenant]
    ON [Notifications].[MailServer]([TenantId] ASC);
