\set ON_ERROR_STOP on
BEGIN;

DO $$
BEGIN
    IF ARRAY(SELECT "MigrationId"::text FROM "__EFMigrationsHistory" ORDER BY "MigrationId")
       <> ARRAY['20261028193126_InitialCreate']
       OR to_regclass('contacts') IS NOT NULL THEN
        RAISE EXCEPTION 'Expected the beta.1 schema';
    END IF;
END $$;

LOCK TABLE campaigns, recipients, deliveries, delivery_status_journal IN ACCESS EXCLUSIVE MODE;

DO $$
BEGIN
    IF EXISTS (SELECT FROM campaigns) OR EXISTS (SELECT FROM recipients)
       OR EXISTS (SELECT FROM deliveries) OR EXISTS (SELECT FROM delivery_status_journal) THEN
        RAISE EXCEPTION 'Expected an empty rehearsal database';
    END IF;
END $$;

INSERT INTO campaigns (id, name, message_text, status, created_at, started_at)
SELECT md5('campaign-' || n)::uuid, 'Rehearsal ' || n, 'Synthetic rehearsal message',
       CASE WHEN n = 101 THEN 0 ELSE 4 END,
       '2026-01-01 00:00 UTC', CASE WHEN n < 101 THEN '2026-01-01 01:00 UTC'::timestamptz END
FROM generate_series(1, 101) AS n;

INSERT INTO recipients (id, email, created_at, updated_at)
SELECT md5('recipient-' || n)::uuid,
       CASE WHEN n % 10 = 0 THEN E' \tRecipient-' || n || '@EXAMPLE.INVALID\t '
            ELSE 'recipient-' || n || '@example.invalid' END,
       '2026-01-01 00:00 UTC', '2026-01-01 01:00 UTC'
FROM generate_series(1, 10000) AS n;

WITH sample AS (
    SELECT campaign, recipient, (campaign + recipient) % 4 AS kind
    FROM generate_series(1, 100) AS campaign
    CROSS JOIN generate_series(1, 10000) AS recipient
)
INSERT INTO deliveries (id, campaign_id, recipient_id, recipient_email, delivery_status,
                        created_at, updated_at, completed_at, worker_id, lock_expires_at)
SELECT md5('delivery-' || campaign || '-' || recipient)::uuid,
       md5('campaign-' || campaign)::uuid, md5('recipient-' || recipient)::uuid,
       CASE recipient % 100 WHEN 0 THEN '' WHEN 1 THEN E' \t '
           WHEN 2 THEN E' \tHistorical-' || recipient || '@EXAMPLE.INVALID\t '
           ELSE 'historical-' || recipient || '@example.invalid' END,
       CASE WHEN kind = 3 THEN -1 ELSE kind END,
       '2026-01-01 01:00 UTC', '2026-01-01 02:00 UTC',
       CASE WHEN kind IN (2, 3) THEN '2026-01-01 02:00 UTC'::timestamptz END,
       CASE WHEN kind = 1 THEN 'synthetic-worker' END,
       CASE WHEN kind = 1 THEN
           CASE WHEN campaign % 2 = 0 THEN '2099-01-01 02:00 UTC'::timestamptz
                ELSE '2099-01-01 03:00 UTC'::timestamptz END
       END
FROM sample;

INSERT INTO delivery_status_journal (campaign_id, delivery_id, previous_status, new_status,
                                     worker_id, occurred_at, operator_result)
SELECT campaign_id, id, 0, 1, 'synthetic-worker', '2026-01-01 01:30 UTC', NULL
FROM deliveries WHERE delivery_status <> 0 ORDER BY id;

INSERT INTO delivery_status_journal (campaign_id, delivery_id, previous_status, new_status,
                                     worker_id, occurred_at, operator_result)
SELECT campaign_id, id, 1, delivery_status, 'synthetic-worker', updated_at,
       CASE WHEN delivery_status = 2 THEN 'AcceptedByProvider' ELSE 'SimulatedOperatorRejected' END
FROM deliveries WHERE delivery_status IN (-1, 2) ORDER BY id;

DO $$
BEGIN
    IF (SELECT count(*) FROM campaigns) <> 101
       OR (SELECT count(*) FROM recipients) <> 10000
       OR (SELECT count(*) FROM deliveries) <> 1000000
       OR (SELECT count(*) FROM delivery_status_journal) <> 1250000 THEN
        RAISE EXCEPTION 'Unexpected rehearsal sample size';
    END IF;
END $$;

COMMIT;
