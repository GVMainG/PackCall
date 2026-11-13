\set ON_ERROR_STOP on
SET TIME ZONE 'UTC';

\if :migrated
CREATE TEMP VIEW people AS
SELECT c.id, c.created_at, c.updated_at, ch.address AS email, c.display_name,
       ch.channel, ch.id AS channel_id
FROM contacts c LEFT JOIN contact_channels ch ON ch.contact_id = c.id;
CREATE TEMP VIEW history AS
SELECT id, campaign_id, contact_id AS person_id, address_snapshot AS address,
       delivery_status, created_at, updated_at, completed_at, worker_id, lock_expires_at, channel
FROM deliveries;

DO $$
BEGIN
    IF EXISTS (
        (SELECT DISTINCT campaign_id, contact_id FROM deliveries
         EXCEPT SELECT campaign_id, contact_id FROM campaign_contacts)
        UNION ALL
        (SELECT campaign_id, contact_id FROM campaign_contacts
         EXCEPT SELECT DISTINCT campaign_id, contact_id FROM deliveries)
    ) OR EXISTS (
        (SELECT DISTINCT campaign_id, channel FROM deliveries
         EXCEPT SELECT campaign_id, channel FROM campaign_channels)
        UNION ALL
        (SELECT campaign_id, channel FROM campaign_channels
         EXCEPT SELECT DISTINCT campaign_id, channel FROM deliveries)
    ) THEN
        RAISE EXCEPTION 'Campaign snapshot differs from actual deliveries';
    END IF;
END $$;
\else
CREATE TEMP VIEW people AS
SELECT id, created_at, updated_at,
       CASE WHEN :normalized THEN lower(btrim(email, E' \t\r\n')) ELSE email END AS email,
       NULL::text AS display_name, 'email' AS channel, id AS channel_id
FROM recipients;
CREATE TEMP VIEW history AS
SELECT id, campaign_id, recipient_id AS person_id,
       CASE WHEN :normalized THEN nullif(lower(btrim(recipient_email, E' \t\r\n')), '')
            ELSE recipient_email END AS address,
       delivery_status, created_at, updated_at, completed_at, worker_id, lock_expires_at, 'email' AS channel
FROM deliveries;
\endif

-- Hash row JSON in primary-key order, retaining nulls without exporting addresses or messages.
SELECT 'people', count(*), md5(string_agg(md5(row_to_json(p)::text), '' ORDER BY p.id, p.channel_id))
FROM people p;
SELECT 'history', count(*),
       md5(string_agg(md5(row_to_json(h)::text), '' ORDER BY h.id))
FROM (SELECT id, campaign_id, person_id, delivery_status, created_at, updated_at,
             completed_at, worker_id, lock_expires_at, channel FROM history) h;
SELECT 'snapshots', count(*), md5(string_agg(md5(row_to_json(a)::text), '' ORDER BY a.id))
FROM (SELECT id, address FROM history) a;
SELECT 'campaigns', count(*), md5(string_agg(md5(row_to_json(c)::text), '' ORDER BY c.id))
FROM campaigns c;
SELECT 'journal', count(*), md5(string_agg(md5(row_to_json(j)::text), '' ORDER BY j.id)),
       (SELECT last_value FROM delivery_status_journal_id_seq),
       (SELECT is_called FROM delivery_status_journal_id_seq)
FROM delivery_status_journal j;
SELECT 'statuses', delivery_status, count(*), count(*) FILTER (WHERE worker_id IS NOT NULL),
       count(*) FILTER (WHERE lock_expires_at IS NOT NULL),
       count(*) FILTER (WHERE address IS NULL)
FROM history GROUP BY delivery_status ORDER BY delivery_status;
SELECT 'stranded', count(*), count(DISTINCT campaign_id)
FROM history JOIN campaigns c ON campaign_id = c.id
WHERE delivery_status IN (0, 1) AND address IS NULL AND c.status IN (0, 1, 3, 4);
SELECT 'empty_campaigns', count(*) FROM campaigns c
WHERE NOT EXISTS (SELECT FROM deliveries d WHERE d.campaign_id = c.id);
