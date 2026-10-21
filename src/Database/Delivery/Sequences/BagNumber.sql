-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- SEQUENCE: Delivery.BagNumber
-- Purpose: Bag numbers (BG-100001), drawn by the default on Delivery.Bag.Number. Shared by all tenants.
-- Author: Courier team
-- Date: 2026-10-21
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE SEQUENCE [Delivery].[BagNumber]
    AS BIGINT
    START WITH 100001
    INCREMENT BY 1;
