-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 013_ClearOldLockouts.sql
-- Purpose: Wrong passwords no longer lock a login (2026-10-16), but a login the old rule had already locked stays
--          locked in the database, and sign-in refuses a lock whatever the setting, so it would never let that person
--          in again. Every login that was not stopped by an admin or an owner (Archived = 0) is let back in here, once
--          per database. A stopped login keeps its lock, which is what stopping means: it is archived and locked to the
--          end of time by IUserAccounts.SetLoginEnabledAsync, and only "let back in" undoes it.
-- Author: Courier team
-- Date: 2026-10-16
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
UPDATE
    [Identity].[User]
SET
    LockoutEnd = NULL,
    AccessFailedCount = 0
WHERE
    Archived = 0 AND
    (LockoutEnd IS NOT NULL OR AccessFailedCount > 0);
