-- ============================================================================
-- 02_agreement.sql: run after each night is loaded.
--
-- Re-derives the 30-day mean, min and max and the 7-day trend from
-- daily_flow in T-SQL and requires them to equal what the batch job wrote
-- for that night. The COBOL, the C# and the database each compute the same
-- answer on their own. Any difference throws, and the CI run stops.
-- ============================================================================
SET NOCOUNT ON;

IF OBJECT_ID('sierra.check_agreement') IS NULL
CREATE TABLE sierra.check_agreement (
    run_date       date          NOT NULL,
    site_id        varchar(15)   NOT NULL,
    job_mean_30    decimal(9,2)  NULL,
    sql_mean_30    decimal(9,2)  NULL,
    job_min_30     decimal(9,2)  NULL,
    sql_min_30     decimal(9,2)  NULL,
    job_max_30     decimal(9,2)  NULL,
    sql_max_30     decimal(9,2)  NULL,
    job_trend_pct  decimal(7,1)  NULL,
    sql_trend_pct  decimal(7,1)  NULL,
    verdict        varchar(5)    NOT NULL,
    CONSTRAINT pk_check_agreement PRIMARY KEY (run_date, site_id)
);

DECLARE @run date = (SELECT MAX(run_date) FROM sierra.run_result);

-- ---------------------------------------------------------------------------
-- AGREEMENT FOR THE NIGHT JUST LOADED
-- ---------------------------------------------------------------------------
WITH ranked AS (
    SELECT f.site_id, f.flow_date, f.cfs,
           ROW_NUMBER() OVER (PARTITION BY f.site_id ORDER BY f.flow_date DESC) AS rn
    FROM sierra.daily_flow AS f
    JOIN sierra.run_result AS r ON r.site_id = f.site_id AND r.run_date = @run
    WHERE f.flow_date <= r.last_date
),
derived AS (
    SELECT site_id,
           COUNT(CASE WHEN rn <= 30 THEN 1 END)                                              AS days_30,
           ROUND(CAST(SUM(CASE WHEN rn <= 30 THEN cfs END) AS decimal(18,2))
                 / COUNT(CASE WHEN rn <= 30 THEN 1 END), 2)                                  AS mean_30,
           MIN(CASE WHEN rn <= 30 THEN cfs END)                                              AS min_30,
           MAX(CASE WHEN rn <= 30 THEN cfs END)                                              AS max_30,
           -- the COBOL rounds each 7-day mean to cents before comparing them, so this does too
           CAST(ROUND(CAST(SUM(CASE WHEN rn <= 7 THEN cfs END) AS decimal(18,2)) / 7, 2) AS decimal(18,2))          AS mean_a,
           CAST(ROUND(CAST(SUM(CASE WHEN rn BETWEEN 8 AND 14 THEN cfs END) AS decimal(18,2)) / 7, 2) AS decimal(18,2)) AS mean_b,
           COUNT(*)                                                                         AS n_all
    FROM ranked
    GROUP BY site_id
),
compared AS (
    SELECT r.site_id,
           r.mean_30 AS job_mean_30, d.mean_30 AS sql_mean_30,
           r.min_30  AS job_min_30,  d.min_30  AS sql_min_30,
           r.max_30  AS job_max_30,  d.max_30  AS sql_max_30,
           r.trend_pct AS job_trend_pct,
           CASE WHEN d.n_all < 14 OR d.mean_b = 0 THEN CAST(0 AS decimal(6,1))
                ELSE CAST(CASE WHEN ROUND((d.mean_a - d.mean_b) * 100 / d.mean_b, 1) > 99999.9
                               THEN 9999.9      -- the COBOL's size-error value, copied on purpose (finding 6)
                               ELSE ROUND((d.mean_a - d.mean_b) * 100 / d.mean_b, 1) END AS decimal(7,1))
           END AS sql_trend_pct
    FROM sierra.run_result AS r
    JOIN derived AS d ON d.site_id = r.site_id
    WHERE r.run_date = @run AND r.class <> 'NO DATA'
)
SELECT site_id, job_mean_30, sql_mean_30, job_min_30, sql_min_30, job_max_30, sql_max_30,
       job_trend_pct, sql_trend_pct,
       CASE WHEN job_mean_30 = sql_mean_30 AND job_min_30 = sql_min_30 AND job_max_30 = sql_max_30
                 AND ISNULL(job_trend_pct, 0) = sql_trend_pct THEN 'MATCH' ELSE 'DIFF' END AS verdict
INTO #agreement
FROM compared;

-- kept for 04_export.sql and the web page
DELETE FROM sierra.check_agreement WHERE run_date = @run;
INSERT INTO sierra.check_agreement (run_date, site_id, job_mean_30, sql_mean_30, job_min_30, sql_min_30,
    job_max_30, sql_max_30, job_trend_pct, sql_trend_pct, verdict)
SELECT @run, site_id, job_mean_30, sql_mean_30, job_min_30, sql_min_30, job_max_30, sql_max_30,
       job_trend_pct, sql_trend_pct, verdict
FROM #agreement;

SELECT * FROM #agreement WHERE verdict = 'DIFF' ORDER BY site_id;

DECLARE @diffs int = (SELECT COUNT(*) FROM #agreement WHERE verdict = 'DIFF');
DECLARE @checked int = (SELECT COUNT(*) FROM #agreement);
PRINT CONCAT('AGREEMENT  ', CONVERT(char(10), @run, 23), '  ', @checked - @diffs, ' OF ', @checked, ' GAGES MATCH THE BATCH JOB');
IF @diffs > 0
    THROW 50008, 'T-SQL and the batch job disagree. See the DIFF rows above.', 1;
DROP TABLE #agreement;

