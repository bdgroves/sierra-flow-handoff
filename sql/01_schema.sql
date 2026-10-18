-- ============================================================================
-- SIERRA-FLOW on SQL Server / Azure SQL
-- 01_schema.sql: tables for the nightly load. Safe to run more than once.
--
--   sierra.gage           the gages, each with a geography point (WGS 84)
--   sierra.daily_flow     every USGS daily mean ever loaded, one row per gage-day
--   sierra.flow_revision  every insert and change to daily_flow, from MERGE ... OUTPUT
--   sierra.run_result     one row per gage per night: results.csv, kept forever
-- ============================================================================
SET NOCOUNT ON;
IF SCHEMA_ID('sierra') IS NULL EXEC('CREATE SCHEMA sierra');
GO

IF OBJECT_ID('sierra.gage') IS NULL
CREATE TABLE sierra.gage (
    site_id      varchar(15)   NOT NULL CONSTRAINT pk_gage PRIMARY KEY,
    short_name   varchar(40)   NOT NULL,
    basin        varchar(20)   NOT NULL,
    basin_seq    tinyint       NOT NULL,
    seq          tinyint       NOT NULL,           -- 1 = highest on the river
    drain_sqmi   decimal(9,1)  NULL,
    regulation   varchar(10)   NOT NULL CONSTRAINT ck_gage_reg CHECK (regulation IN ('NATURAL', 'REGULATED')),
    usgs_name    varchar(80)   NULL,
    location     geography     NOT NULL,
    CONSTRAINT uq_gage_order UNIQUE (basin, seq)   -- the COBOL sort needs this to be true; here it is enforced
);
GO

IF OBJECT_ID('sierra.daily_flow') IS NULL
CREATE TABLE sierra.daily_flow (
    site_id      varchar(15)   NOT NULL CONSTRAINT fk_flow_gage REFERENCES sierra.gage (site_id),
    flow_date    date          NOT NULL,
    cfs          decimal(9,2)  NOT NULL CONSTRAINT ck_flow_cfs CHECK (cfs >= 0),   -- finding 3: the COBOL drops the sign
    approval     char(1)       NULL,                -- P = provisional, A = approved
    loaded_utc   datetime2(0)  NOT NULL,
    CONSTRAINT pk_daily_flow PRIMARY KEY (site_id, flow_date)
);
GO

IF OBJECT_ID('sierra.flow_revision') IS NULL
CREATE TABLE sierra.flow_revision (
    revision_id  bigint IDENTITY CONSTRAINT pk_flow_revision PRIMARY KEY,
    action       nvarchar(10)  NOT NULL,            -- INSERT or UPDATE
    site_id      varchar(15)   NOT NULL,
    flow_date    date          NOT NULL,
    old_cfs      decimal(9,2)  NULL,
    new_cfs      decimal(9,2)  NULL,
    old_approval char(1)       NULL,
    new_approval char(1)       NULL,
    changed_utc  datetime2(0)  NOT NULL
);
GO

IF OBJECT_ID('sierra.run_result') IS NULL
CREATE TABLE sierra.run_result (
    run_date        date          NOT NULL,
    site_id         varchar(15)   NOT NULL CONSTRAINT fk_result_gage REFERENCES sierra.gage (site_id),
    last_date       date          NULL,
    last_cfs        decimal(9,2)  NULL,
    n_years         tinyint       NULL,
    p10             decimal(9,2)  NULL,
    p25             decimal(9,2)  NULL,
    p50             decimal(9,2)  NULL,
    p75             decimal(9,2)  NULL,
    p90             decimal(9,2)  NULL,
    class           varchar(17)   NOT NULL,
    pct_median      decimal(6,1)  NULL,
    days_30         tinyint       NULL,
    mean_30         decimal(9,2)  NULL,
    min_30          decimal(9,2)  NULL,
    max_30          decimal(9,2)  NULL,
    pct_normal_30   decimal(6,1)  NULL,
    days_below_p10  tinyint       NULL,
    days_above_p90  tinyint       NULL,
    trend           varchar(7)    NULL,
    trend_pct       decimal(6,1)  NULL,
    stale           char(1)       NOT NULL,
    run_time        time(0)       NOT NULL,
    rc              tinyint       NOT NULL,
    CONSTRAINT pk_run_result PRIMARY KEY (run_date, site_id)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'sx_gage_location')
    CREATE SPATIAL INDEX sx_gage_location ON sierra.gage (location);
GO
