-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 012_DemoAdminPassword.sql
-- Purpose: The live site was started before it knew a login: its super admin admin@onedrop.test existed with a password
--          nobody had, and was locked out by the tries to guess it. Once, on each database this script runs on, that
--          one login (a TenantAdmin of the courier onedrop) gets the demo password OneDrop#2026 again and is let back
--          in, so the site can be signed in to without anyone touching the server. The hash is ASP.NET Core Identity's
--          own format (v3, PBKDF2 with HMAC-SHA256, 100000 rounds, its own salt), and the password is public: change it
--          at /Account/Password after signing in. It creates no login: on a database without admin@onedrop.test, which
--          is a new one, the app makes it or not as Setup:AdminEmail says, and a script that has run is never run again,
--          so a password changed afterwards stays changed.
-- Author: Courier team
-- Date: 2026-10-16
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
UPDATE
    u
SET
    PasswordHash = N'AQAAAAEAAYagAAAAEJ41LIapgikdGBOgmRcZtZo3HmLPd4EI+TV/Qz0ibUnvM0WC6OTyBRau32mv+Vp0QQ==',
    SecurityStamp = CONVERT(NVARCHAR (36), NEWID()),
    ConcurrencyStamp = CONVERT(NVARCHAR (36), NEWID()),
    LockoutEnd = NULL,
    AccessFailedCount = 0
FROM
    [Identity].[User] u
    JOIN
    Platform.Tenant t
        ON t.Id = u.TenantId
WHERE
    t.Slug = N'onedrop' AND
    u.NormalizedUserName = N'ADMIN@ONEDROP.TEST' AND
    EXISTS (
        SELECT 1
        FROM
            [Identity].UserRole ur
            JOIN
            [Identity].[Role] r
                ON r.Id = ur.RoleId
        WHERE
            ur.UserId = u.Id AND
            r.NormalizedName = N'TENANTADMIN');
