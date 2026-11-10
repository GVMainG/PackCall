SELECT coalesce(json_agg(row_to_json(t)), '[]'::json) FROM (
 SELECT relname, pg_table_size(relid) AS table_bytes, pg_indexes_size(relid) AS index_bytes,
 n_live_tup, n_dead_tup, vacuum_count, autovacuum_count, analyze_count, autoanalyze_count,
 extract(epoch FROM last_vacuum) AS last_vacuum_s,
 extract(epoch FROM last_autovacuum) AS last_autovacuum_s,
 extract(epoch FROM last_analyze) AS last_analyze_s,
 extract(epoch FROM last_autoanalyze) AS last_autoanalyze_s
 FROM pg_stat_user_tables WHERE relname IN ('deliveries','delivery_status_journal','campaigns')
) t;
