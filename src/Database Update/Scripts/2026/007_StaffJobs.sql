-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 007_StaffJobs.sql
-- Purpose: Staff now have a job and permissions (Identity.StaffMember). Every hub login made before then could do any
--          hub job at any hub, so each becomes relief staff at head office (Job 7) with every hub desk
--          (Permissions 31: receive, dispatch, delivery desk, returns, cash desk) and keeps exactly what it had.
-- Author: Courier team
-- Date: 2026-10-12
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
INSERT INTO [Identity].StaffMember (TenantId, UserId, HubId, Job, [Permissions])
SELECT
    u.TenantId,
    u.Id,
    NULL,
    7,
    31
FROM
    [Identity].[User] u
    JOIN
    [Identity].UserRole ur
        ON ur.UserId = u.Id
    JOIN
    [Identity].[Role] r
        ON r.Id = ur.RoleId
WHERE
    r.NormalizedName = 'HUBSTAFF' AND
    u.TenantId IS NOT NULL AND
    NOT EXISTS (SELECT 1 FROM [Identity].StaffMember s WHERE s.UserId = u.Id);
