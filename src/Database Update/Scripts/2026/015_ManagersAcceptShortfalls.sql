-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 015_ManagersAcceptShortfalls.sql
-- Purpose: A rider's cash shortfall now waits for a hub manager to accept it (permission 1024, accept cash shortfalls),
--          which the hub manager (Job 1) and operations manager (Job 8) presets now include. Staff made before keep
--          the permissions they were given, so those two jobs are given it here.
-- Author: Courier team
-- Date: 2026-10-21
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
UPDATE
    [Identity].StaffMember
SET
    [Permissions] = [Permissions] | 1024
WHERE
    Job IN (1, 8);
