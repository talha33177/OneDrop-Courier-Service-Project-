-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Payments.PayoutRequest
-- Purpose: A merchant asking to be paid its balance now, today's cash too, instead of waiting for the next morning's
--          payout; the courier's admin decides. AmountAsked is the balance when it asked. Status (TINYINT enum
--          Domain.Payments.PayoutRequestStatus): 1 Open, 2 Paid (PayoutId is the payout the admin made), 3 Refused
--          (Answer says why), 4 Withdrawn by the merchant. One open request per business
--          (UX_PayoutRequest_Merchant_Open)
-- Author: Courier team
-- Date: 2026-10-08
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Payments].[PayoutRequest] (
    [Id]          BIGINT          IDENTITY (1, 1) NOT NULL,
    [TenantId]    BIGINT          NOT NULL,
    [MerchantId]  BIGINT          NOT NULL,
    [AmountAsked] DECIMAL (12, 2) NOT NULL,
    [Status]      TINYINT         NOT NULL,
    [Answer]      NVARCHAR (200)  NULL,
    [AnsweredOn]  DATETIME2 (7)   NULL,
    [PayoutId]    BIGINT          NULL,
    [RowVersion]  ROWVERSION      NOT NULL,
    [UpdatedId]   BIGINT          NULL,
    [UpdatedOn]   DATETIME2 (7)   DEFAULT (getutcdate()) NOT NULL,
    [Created]     DATETIME2 (0)   DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_PayoutRequest_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_PayoutRequest_Merchant] FOREIGN KEY ([MerchantId]) REFERENCES [Merchants].[Merchant] ([Id]),
    CONSTRAINT [FK_PayoutRequest_Payout] FOREIGN KEY ([PayoutId]) REFERENCES [Payments].[Payout] ([Id]),
    CONSTRAINT [FK_PayoutRequest_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_PayoutRequest_Amount] CHECK ([AmountAsked] > (0)),
    CONSTRAINT [chk_PayoutRequest_Status] CHECK ([Status] BETWEEN 1 AND 4),
    CONSTRAINT [chk_PayoutRequest_Answer] CHECK (([Status] = 1 AND [AnsweredOn] IS NULL) OR ([Status] > 1 AND [AnsweredOn] IS NOT NULL)),
    CONSTRAINT [chk_PayoutRequest_Payout] CHECK (([Status] = 2 AND [PayoutId] IS NOT NULL) OR ([Status] <> 2 AND [PayoutId] IS NULL))
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_PayoutRequest_Merchant_Open]
    ON [Payments].[PayoutRequest]([MerchantId] ASC) WHERE [Status] = 1;


GO
CREATE NONCLUSTERED INDEX [IX_PayoutRequest_Tenant_Status]
    ON [Payments].[PayoutRequest]([TenantId] ASC, [Status] ASC);


GO
CREATE NONCLUSTERED INDEX [IX_PayoutRequest_Merchant]
    ON [Payments].[PayoutRequest]([MerchantId] ASC, [Id] ASC);
