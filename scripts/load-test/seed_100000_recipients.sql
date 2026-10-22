-- Test recipients for PackCall. Requires the schema created at API startup.
-- PostgreSQL generates all 100000 rows; safe to run again.
BEGIN;

INSERT INTO recipients (id, email, created_at, updated_at)
SELECT gen_random_uuid(),
       'loadtest-' || lpad(n::text, 6, '0') || '@packcall.test',
       now(),
       now()
FROM generate_series(1, 10000) AS numbers(n)
ON CONFLICT (email) DO NOTHING;

COMMIT;

SELECT count(*) AS loadtest_recipients
FROM recipients
WHERE email LIKE 'loadtest-%@packcall.test';
