SELECT json_build_object(
 'db_now', statement_timestamp(), 'server_start', pg_postmaster_start_time(),
 'stats_reset', stats_reset,
 'xact_commit', xact_commit, 'xact_rollback', xact_rollback,
 'blocks_hit', blks_hit, 'blocks_read', blks_read,
 'temp_bytes', temp_bytes, 'temp_files', temp_files, 'deadlocks', deadlocks,
 'working_connections', (SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name<>'packcall-performance-collector'),
 'hidden_connections', (SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND application_name<>'packcall-performance-collector' AND state IS NULL),
 'activity', (SELECT coalesce(json_agg(row_to_json(a)), '[]'::json) FROM (
    SELECT state, wait_event_type, wait_event, count(*) AS count FROM pg_stat_activity
    WHERE datname=current_database() AND application_name<>'packcall-performance-collector'
    GROUP BY state, wait_event_type, wait_event
 ) a),
 'track_io_timing', current_setting('track_io_timing'),
 'track_wal_io_timing', current_setting('track_wal_io_timing')
) FROM pg_stat_database WHERE datname=current_database();
