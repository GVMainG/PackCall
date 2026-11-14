-- The driver creates the campaign through the API after this transaction commits.
BEGIN;
LOCK TABLE contacts, contact_channels, campaigns, campaign_contacts, campaign_channels,
    deliveries, delivery_status_journal IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
 IF EXISTS(SELECT FROM contacts) OR EXISTS(SELECT FROM contact_channels)
    OR EXISTS(SELECT FROM campaigns) OR EXISTS(SELECT FROM campaign_contacts)
    OR EXISTS(SELECT FROM campaign_channels) OR EXISTS(SELECT FROM deliveries)
    OR EXISTS(SELECT FROM delivery_status_journal) THEN
   RAISE EXCEPTION 'performance_database_not_empty';
 END IF;
END $$;
WITH seed AS (
    SELECT md5('packcall-performance-' || n)::uuid AS id,
           'perf' || lpad(n::text, 9, '0') || '@example.invalid' AS address
    FROM generate_series(1, :count::integer) n
), inserted AS (
    INSERT INTO contacts (id, created_at, updated_at)
    SELECT id, statement_timestamp(), statement_timestamp() FROM seed
    RETURNING id
)
INSERT INTO contact_channels (id, contact_id, channel, address)
SELECT inserted.id, inserted.id, 'email', seed.address FROM inserted JOIN seed USING (id);
COMMIT;
