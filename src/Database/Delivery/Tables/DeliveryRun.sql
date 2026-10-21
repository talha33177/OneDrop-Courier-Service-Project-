-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Delivery.DeliveryRun
-- Purpose: A rider's run sheet for one day (RunDate, the tenant's date): the parcels the hub gave them to deliver are
--          Delivery.DeliveryAttempt rows on it. Status (TINYINT enum Domain.Delivery.RunStatus): 1 Open, 2 Closed, when
--          the hub counted the rider's cash (CashExpected against CashReceived) and took back the parcels not
--          delivered. One run per rider a day. A run closed short waits for the hub manager, who accepts the shortfall
--          (ShortAcceptedById at ShortAcceptedOn) and says what was done about it (ShortNote).
-- Author: Courier team
-- Date: 2026-10-04
--          2026-10-21: ShortAcceptedOn, ShortAcceptedById, ShortNote.
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Delivery].[DeliveryRun] (
    [Id]           BIGINT          IDENTITY (1, 1) NOT NULL,
    [TenantId]     BIGINT          NOT NULL,
    [RiderId]      BIGINT          NOT NULL,
    [HubId]        BIGINT          NOT NULL,
    [RunDate]      DATE            NOT NULL,
    [Status]       TINYINT         NOT NULL,
    [CashExpected] DECIMAL (12, 2) NULL,
    [CashReceived] DECIMAL (12, 2) NULL,
    [ClosedOn]     DATETIME2 (7)   NULL,
    [ShortAcceptedOn]   DATETIME2 (7)   NULL,
    [ShortAcceptedById] BIGINT          NULL,
    [ShortNote]         NVARCHAR (200)  NULL,
    [RowVersion]   ROWVERSION      NOT NULL,
    [UpdatedId]    BIGINT          NULL,
    [UpdatedOn]    DATETIME2 (7)   DEFAULT (getutcdate()) NOT NULL,
    [Created]      DATETIME2 (0)   DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_DeliveryRun_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_DeliveryRun_Rider] FOREIGN KEY ([RiderId]) REFERENCES [Delivery].[Rider] ([Id]),
    CONSTRAINT [FK_DeliveryRun_Hub] FOREIGN KEY ([HubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_DeliveryRun_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [FK_DeliveryRun_User_ShortAcceptedById] FOREIGN KEY ([ShortAcceptedById]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [chk_DeliveryRun_Short] CHECK (([ShortAcceptedOn] IS NULL AND [ShortNote] IS NULL) OR ([ShortAcceptedOn] IS NOT NULL AND [ShortNote] IS NOT NULL AND [CashReceived] < [CashExpected])),
    CONSTRAINT [chk_DeliveryRun_Status] CHECK ([Status] BETWEEN 1 AND 2),
    CONSTRAINT [chk_DeliveryRun_Cash] CHECK (([Status] = 1 AND [CashReceived] IS NULL) OR ([Status] = 2 AND [CashExpected] >= (0) AND [CashReceived] >= (0)))
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_DeliveryRun_Rider_RunDate]
    ON [Delivery].[DeliveryRun]([RiderId] ASC, [RunDate] ASC);


GO
CREATE NONCLUSTERED INDEX [IX_DeliveryRun_Tenant_Hub_Status]
    ON [Delivery].[DeliveryRun]([TenantId] ASC, [HubId] ASC, [Status] ASC);
