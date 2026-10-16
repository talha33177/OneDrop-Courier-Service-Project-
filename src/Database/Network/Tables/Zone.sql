-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- TABLE: Network.Zone
-- Purpose: A part of the coverage map served by one hub: a city area (Mirpur), a suburb (Savar) or a district
--          (Sylhet). City and IsSuburb decide a parcel's service area: another city is outside the city, a suburb of
--          the pickup's city is a suburb, the rest inside the city (Domain.Pricing.ServiceAreas).
--          2026-10-16: Division (one of the eight; NULL: not filed) is where the address picker lists the zone.
-- Author: Courier team
-- Date: 2026-10-04
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE TABLE [Network].[Zone] (
    [Id]        BIGINT         IDENTITY (1, 1) NOT NULL,
    [TenantId]  BIGINT         NOT NULL,
    [Code]      NVARCHAR (20)  NOT NULL,
    [Name]      NVARCHAR (200) NOT NULL,
    [HubId]     BIGINT         NOT NULL,
    [City]      NVARCHAR (60)  NOT NULL,
    [IsSuburb]  BIT            NOT NULL,
    [Division]  NVARCHAR (30)  NULL,
    [Archived]  BIT            DEFAULT ((0)) NOT NULL,
    [UpdatedId] BIGINT         NULL,
    [UpdatedOn] DATETIME2 (7)  DEFAULT (getutcdate()) NOT NULL,
    [Created]   DATETIME2 (0)  DEFAULT (getutcdate()) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_Zone_Tenant] FOREIGN KEY ([TenantId]) REFERENCES [Platform].[Tenant] ([Id]),
    CONSTRAINT [FK_Zone_Hub] FOREIGN KEY ([HubId]) REFERENCES [Network].[Hub] ([Id]),
    CONSTRAINT [FK_Zone_User] FOREIGN KEY ([UpdatedId]) REFERENCES [Identity].[User] ([Id])
);


GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_Zone_Tenant_Code]
    ON [Network].[Zone]([TenantId] ASC, [Code] ASC);


GO
CREATE NONCLUSTERED INDEX [IX_Zone_HubId]
    ON [Network].[Zone]([HubId] ASC);
