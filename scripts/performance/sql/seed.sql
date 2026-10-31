-- Only the new isolated database is accepted by the driver. Legacy seed remains untouched.
BEGIN;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM recipients) OR EXISTS(SELECT 1 FROM campaigns) THEN
   RAISE EXCEPTION 'performance_database_not_empty';
 END IF;
END $$;
INSERT INTO recipients(id,email,created_at,updated_at)
SELECT md5('packcall-performance-' || n)::uuid,
       'perf' || lpad(n::text, 9, '0') || '@example.invalid', statement_timestamp(), statement_timestamp()
FROM generate_series(1, :count::integer) n;
COMMIT;
