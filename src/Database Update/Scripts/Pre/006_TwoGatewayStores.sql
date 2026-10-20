-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- Script: 006_TwoGatewayStores.sql
-- Purpose: A courier keeps a sandbox SSLCommerz store and a live one side by side, and picks which takes payments.
--          The one store each courier had (StoreId, StorePassword) moves into the sandbox or the live slot by its
--          IsLive, which stays the one in use; the two old columns are dropped. Runs before the publish, against the
--          old schema; does nothing on a new database or once it is done. The statements naming the new columns run
--          through EXEC, because the batch is compiled before they exist
-- Author: Courier team
-- Date: 2026-10-20
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
IF OBJECT_ID(N'Payments.GatewayStore', N'U') IS NOT NULL AND
   COL_LENGTH(N'Payments.GatewayStore', N'StoreId') IS NOT NULL
BEGIN
    ALTER TABLE Payments.GatewayStore ADD
        [SandboxStoreId]  NVARCHAR (100)  NULL,
        [SandboxPassword] NVARCHAR (2000) NULL,
        [LiveStoreId]     NVARCHAR (100)  NULL,
        [LivePassword]    NVARCHAR (2000) NULL;

    EXEC (N'
        UPDATE
            Payments.GatewayStore
        SET
            SandboxStoreId = CASE WHEN IsLive = 0 THEN StoreId END,
            SandboxPassword = CASE WHEN IsLive = 0 THEN StorePassword END,
            LiveStoreId = CASE WHEN IsLive = 1 THEN StoreId END,
            LivePassword = CASE WHEN IsLive = 1 THEN StorePassword END;');

    EXEC (N'ALTER TABLE Payments.GatewayStore DROP COLUMN [StoreId], [StorePassword];');
END;
