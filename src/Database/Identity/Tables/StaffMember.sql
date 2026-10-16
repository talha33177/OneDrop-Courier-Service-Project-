-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Identity.StaffMember
-- Purpose: One of the courier's staff below super admin (a login in the HubStaff role): the hub they work at (NULL
--          for head office, which works at every hub), their job (Domain.Staff.StaffJob) and what they may do
--          (Permissions is a bit field; see Domain.Staff.StaffPermissions): the job's own permissions plus any the
--          admin ticked. Super admins (TenantAdmin) need no row. Stopping someone locks the login, not this row.
-- Author: Courier team
-- Date: 2026-10-12
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Identity].[StaffMember] (
    [Id]          BIGINT        IDENTITY (1, 1) NOT NULL,
    [TenantId]    BIGINT        NOT NULL,
    [UserId]      BIGINT        NOT NULL,
    [HubId]       BIGINT        NULL,
    [Job]         TINYINT       NOT NULL,
    [Permissions] INT           DEFAULT ((0)) NOT NULL,
    [UpdatedId]   BIGINT        NULL,
    [UpdatedOn]   DATETIME2 (7) DEFAULT (getutcdate()) NOT NULL,
    [Created]     DATETIME2 (0) DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_StaffMember_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_StaffMember_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [Identity].[User] ([Id]),
    CONSTRAINT [FK_StaffMember_Hub] FOREIGN KEY ([HubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_StaffMember_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id])
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_StaffMember_Tenant_User]
    ON [Identity].[StaffMember]([TenantId] ASC, [UserId] ASC);


GO
CREATE NONCLUSTERED INDEX [IX_StaffMember_Tenant_Hub]
    ON [Identity].[StaffMember]([TenantId] ASC, [HubId] ASC);
