-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Payments.CashDeposit
-- Purpose: Cash a hub's cash desk paid into the courier's bank (Bank, SlipNumber, DepositedOn, the courier's date),
--          recorded by RecordedById. A hub's cash in hand is the CashReceived of its closed Delivery.DeliveryRun rows
--          less its deposits not refused. Status (TINYINT enum Domain.Payments.CashDepositStatus): 1 Recorded,
--          2 Confirmed by finance (AnsweredById at AnsweredOn), 3 Refused (Answer says what the bank shows; the cash
--          counts as the hub's again). Number (DP-100001) comes from Payments.CashDepositNumber.
-- Author: Courier team
-- Date: 2026-10-21
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Payments].[CashDeposit] (
    [Id]           BIGINT          IDENTITY (1, 1) NOT NULL,
    [TenantId]     BIGINT          NOT NULL,
    [HubId]        BIGINT          NOT NULL,
    [Number]       NVARCHAR (20)   CONSTRAINT [DF_CashDeposit_Number] DEFAULT (concat(N'DP-', NEXT VALUE FOR [Payments].[CashDepositNumber])) NOT NULL,
    [Amount]       DECIMAL (12, 2) NOT NULL,
    [Bank]         NVARCHAR (100)  NOT NULL,
    [SlipNumber]   NVARCHAR (50)   NOT NULL,
    [DepositedOn]  DATE            NOT NULL,
    [Status]       TINYINT         NOT NULL,
    [RecordedById] BIGINT          NULL,
    [AnsweredOn]   DATETIME2 (7)   NULL,
    [AnsweredById] BIGINT          NULL,
    [Answer]       NVARCHAR (200)  NULL,
    [RowVersion]   ROWVERSION      NOT NULL,
    [UpdatedId]    BIGINT          NULL,
    [UpdatedOn]    DATETIME2 (7)   DEFAULT (getutcdate()) NOT NULL,
    [Created]      DATETIME2 (0)   DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_CashDeposit_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_CashDeposit_Hub] FOREIGN KEY ([HubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_CashDeposit_User_RecordedById] FOREIGN KEY ([RecordedById]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [FK_CashDeposit_User_AnsweredById] FOREIGN KEY ([AnsweredById]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [FK_CashDeposit_User_UpdatedId] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_CashDeposit_Amount] CHECK ([Amount] > (0)),
    CONSTRAINT [chk_CashDeposit_Status] CHECK ([Status] BETWEEN 1 AND 3),
    CONSTRAINT [chk_CashDeposit_Answer] CHECK (([Status] = 1 AND [AnsweredOn] IS NULL) OR ([Status] > 1 AND [AnsweredOn] IS NOT NULL)),
    CONSTRAINT [chk_CashDeposit_Refused] CHECK (([Status] = 3 AND [Answer] IS NOT NULL) OR ([Status] <> 3 AND [Answer] IS NULL))
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_CashDeposit_Number]
    ON [Payments].[CashDeposit]([Number] ASC);


GO
CREATE NONCLUSTERED INDEX [IX_CashDeposit_Tenant_Hub_Status]
    ON [Payments].[CashDeposit]([TenantId] ASC, [HubId] ASC, [Status] ASC);
