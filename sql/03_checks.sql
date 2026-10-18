-- ============================================================================
-- 03_checks.sql: after every night is loaded, report on the database.
--
--   1. AGREEMENT  How each night's T-SQL re-derivation compared (02_agreement.sql
--                 already stopped the run on any difference).
--   2. SPATIAL    Distances between gages with the geography type: the next
--                 gage downstream, and each gage's nearest neighbour.
--   3. DATA QA    Gaps in the daily record, provisional vs approved, and how
--                 many values USGS revised since they were first loaded.
-- ============================================================================
SET NOCOUNT ON;

DECLARE @run date = (SELECT MAX(run_date) FROM sierra.run_result);
PRINT CONCAT('CHECKS FOR RUN ', CONVERT(char(10), @run, 23));

-- ---------------------------------------------------------------------------
-- 1. AGREEMENT, EVERY NIGHT (written by 02_agreement.sql as each night loaded)
-- ---------------------------------------------------------------------------
SELECT run_date, COUNT(*) AS gages, SUM(CASE WHEN verdict = 'MATCH' THEN 1 ELSE 0 END) AS matched
FROM sierra.check_agreement
GROUP BY run_date
ORDER BY run_date;

-- ---------------------------------------------------------------------------
-- 2. SPATIAL
-- ---------------------------------------------------------------------------
PRINT 'SPATIAL    GAGE TO THE NEXT GAGE DOWNSTREAM (STRAIGHT LINE, KM)';
SELECT g.basin, g.seq, g.short_name, d.short_name AS next_downstream,
       CAST(g.location.STDistance(d.location) / 1000.0 AS decimal(7,1)) AS km
FROM sierra.gage AS g
OUTER APPLY (
    SELECT TOP (1) n.short_name, n.location
    FROM sierra.gage AS n
    WHERE n.basin = g.basin AND n.seq > g.seq
    ORDER BY n.seq
) AS d
ORDER BY g.basin_seq, g.seq;

PRINT 'SPATIAL    NEAREST OTHER GAGE';
SELECT g.short_name, n.short_name AS nearest, CAST(n.km AS decimal(7,1)) AS km
FROM sierra.gage AS g
CROSS APPLY (
    SELECT TOP (1) o.short_name, g.location.STDistance(o.location) / 1000.0 AS km
    FROM sierra.gage AS o
    WHERE o.site_id <> g.site_id
    ORDER BY g.location.STDistance(o.location)
) AS n
ORDER BY km;

-- ---------------------------------------------------------------------------
-- 3. DATA QA
-- ---------------------------------------------------------------------------
PRINT 'DATA QA    GAPS IN THE DAILY RECORD (LAST 60 DAYS)';
SELECT site_id, gap_after = prev_date, gap_before = flow_date, missing_days = DATEDIFF(day, prev_date, flow_date) - 1
FROM (
    SELECT site_id, flow_date, LAG(flow_date) OVER (PARTITION BY site_id ORDER BY flow_date) AS prev_date
    FROM sierra.daily_flow
    WHERE flow_date >= DATEADD(day, -60, @run)
) AS x
WHERE DATEDIFF(day, prev_date, flow_date) > 1;

PRINT 'DATA QA    PROVISIONAL AND APPROVED DAYS, AND VALUES USGS HAS REVISED';
SELECT g.short_name,
       SUM(CASE WHEN f.approval = 'P' THEN 1 ELSE 0 END) AS provisional_days,
       SUM(CASE WHEN f.approval = 'A' THEN 1 ELSE 0 END) AS approved_days,
       (SELECT COUNT(*) FROM sierra.flow_revision AS v
         WHERE v.site_id = g.site_id AND v.action = 'UPDATE')  AS revisions
FROM sierra.gage AS g
LEFT JOIN sierra.daily_flow AS f ON f.site_id = g.site_id
GROUP BY g.site_id, g.short_name, g.basin_seq, g.seq
ORDER BY g.basin_seq, g.seq;

PRINT 'CHECKS COMPLETE';
