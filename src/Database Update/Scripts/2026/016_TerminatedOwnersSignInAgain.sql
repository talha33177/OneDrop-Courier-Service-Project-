-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 016_TerminatedOwnersSignInAgain.sql
-- Purpose: A terminated merchant account now signs in to read its history (2026-10-22), but accounts terminated before
--          that had their logins shut, so their owners could not sign in to see the warning and their records. The
--          owner's login of every terminated account (a main profile with Archived = 1) is let back in here, once per
--          database, the way IUserAccounts.SetLoginEnabledAsync does it: not archived, no lock, a new security stamp.
--          A moderator's login is left as it is (stopped): the earlier termination stopped every moderator and a stop
--          the owner made cannot be told from it; the courier's admin lets one back in from the merchant's page.
-- Author: Courier team
-- Date: 2026-10-22
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
UPDATE
    u
SET
    Archived = 0,
    LockoutEnabled = 0,
    LockoutEnd = NULL,
    AccessFailedCount = 0,
    SecurityStamp = CONVERT(NVARCHAR(36), NEWID())
FROM
    [Identity].[User] u
    JOIN [Merchants].[Merchant] m
        ON m.Id = u.MerchantId
WHERE
    m.Archived = 1 AND
    m.MainMerchantId IS NULL AND
    u.Archived = 1 AND
    NOT EXISTS (
        SELECT 1
        FROM [Merchants].[Moderator] md
        WHERE md.UserId = u.Id
    );
