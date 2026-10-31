SELECT json_build_object(
 'wal_bytes', (SELECT wal_bytes FROM pg_stat_wal),
 'wal_records', (SELECT wal_records FROM pg_stat_wal),
 'wal_stats_reset', (SELECT stats_reset FROM pg_stat_wal),
 'io_reads', (SELECT sum(reads) FROM pg_stat_io),
 'io_writes', (SELECT sum(writes) FROM pg_stat_io),
 'io_read_bytes', (SELECT sum(read_bytes) FROM pg_stat_io),
 'io_write_bytes', (SELECT sum(write_bytes) FROM pg_stat_io),
 'io_stats_reset', (SELECT max(stats_reset) FROM pg_stat_io),
 'checkpoints_timed', (SELECT num_timed FROM pg_stat_checkpointer),
 'checkpoints_requested', (SELECT num_requested FROM pg_stat_checkpointer),
 'checkpointer_stats_reset', (SELECT stats_reset FROM pg_stat_checkpointer)
);
