-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Merchants.SignUpCode
-- Purpose: A six-digit code emailed to a shop signing up, to prove the email is its own before the account is made.
--          Only the code's SHA-256 hash (with the email) is kept. It works until ExpiresOn (ten minutes), takes five
--          wrong guesses (Attempts) and is used once (VerifiedOn). Email is in lower case. Not merchant-owned: the
--          merchant does not exist yet
-- Author: Courier team
-- Date: 2026-10-18
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Merchants].[SignUpCode] (
    [Id]         BIGINT         IDENTITY (1, 1) NOT NULL,
    [TenantId]   BIGINT         NOT NULL,
    [Email]      NVARCHAR (256) NOT NULL,
    [CodeHash]   NVARCHAR (64)  NOT NULL,
    [IssuedOn]   DATETIME2 (7)  NOT NULL,
    [ExpiresOn]  DATETIME2 (7)  NOT NULL,
    [Attempts]   INT            DEFAULT ((0)) NOT NULL,
    [VerifiedOn] DATETIME2 (7)  NULL,
    [UpdatedId]  BIGINT         NULL,
    [UpdatedOn]  DATETIME2 (7)  DEFAULT (getutcdate()) NOT NULL,
    [Created]    DATETIME2 (0)  DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_SignUpCode_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_SignUpCode_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_SignUpCode_Attempts] CHECK ([Attempts] >= (0))
);


GO
CREATE NONCLUSTERED INDEX [IX_SignUpCode_Tenant_Email]
    ON [Merchants].[SignUpCode]([TenantId] ASC, [Email] ASC, [IssuedOn] DESC);
