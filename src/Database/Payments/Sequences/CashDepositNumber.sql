-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
-- SEQUENCE: Payments.CashDepositNumber
-- Purpose: Hub cash deposit numbers (DP-100001), drawn by the default on Payments.CashDeposit.Number. Shared by all
--          tenants.
-- Author: Courier team
-- Date: 2026-10-21
-- =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
CREATE SEQUENCE [Payments].[CashDepositNumber]
    AS BIGINT
    START WITH 100001
    INCREMENT BY 1;
