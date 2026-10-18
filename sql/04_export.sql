-- ============================================================================
-- 04_export.sql: one JSON document for the web page (docs/data/sql.json).
-- Run with:  sqlcmd ... -y 0 -i 04_export.sql   (sqlcmd refuses -h together with -y 0,
-- so the caller cuts the JSON out from the first { to the last })
-- The outer SELECT wraps FOR JSON in a scalar subquery so it comes back as a
-- single value instead of being split into 2,033-character rows.
-- ============================================================================
SET NOCOUNT ON;
SELECT (
    SELECT
        (SELECT CONVERT(char(10), MAX(run_date), 23) FROM sierra.run_result)          AS run_date,
        (SELECT COUNT(DISTINCT run_date) FROM sierra.run_result)                       AS nights_loaded,
        (SELECT COUNT(*) FROM sierra.daily_flow)                                       AS daily_rows,
        (SELECT COUNT(*) FROM sierra.flow_revision WHERE action = 'UPDATE')            AS revisions,
        (SELECT @@VERSION)                                                             AS server,
        (SELECT COUNT(*) FROM sierra.check_agreement)                                  AS gage_nights_checked,
        (SELECT COUNT(*) FROM sierra.check_agreement WHERE verdict = 'MATCH')          AS gage_nights_matched,
        (SELECT site_id, job_mean_30, sql_mean_30, job_min_30, sql_min_30, job_max_30, sql_max_30,
                job_trend_pct, sql_trend_pct, verdict
           FROM sierra.check_agreement
           WHERE run_date = (SELECT MAX(run_date) FROM sierra.check_agreement)
           ORDER BY site_id
           FOR JSON PATH)                                                              AS agreement,
        (SELECT g.site_id, g.short_name, g.basin, g.seq, g.regulation,
                g.location.Lat AS lat, g.location.Long AS lon,
                d.short_name                                                           AS next_downstream,
                CAST(g.location.STDistance(d.location) / 1000.0 AS decimal(7,1))       AS km_to_next
           FROM sierra.gage AS g
           OUTER APPLY (SELECT TOP (1) n.short_name, n.location FROM sierra.gage AS n
                        WHERE n.basin = g.basin AND n.seq > g.seq ORDER BY n.seq) AS d
           ORDER BY g.basin_seq, g.seq
           FOR JSON PATH)                                                              AS gages,
        (SELECT TOP (60) g.short_name, v.site_id, CONVERT(char(10), v.flow_date, 23) AS flow_date,
                v.old_cfs, v.new_cfs, CONVERT(char(10), v.changed_utc, 23) AS seen_on
           FROM sierra.flow_revision AS v
           JOIN sierra.gage AS g ON g.site_id = v.site_id
           WHERE v.action = 'UPDATE'
           ORDER BY v.revision_id DESC
           FOR JSON PATH)                                                              AS recent_revisions
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
) AS j;
