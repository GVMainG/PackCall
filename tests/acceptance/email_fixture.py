"""Synthetic email campaigns for the isolated worker and operator stands."""
from uuid import UUID


RESET = (
    "TRUNCATE deliveries, campaign_contacts, campaign_channels, campaigns, "
    "contact_channels, contacts, delivery_status_journal"
)


def email_deliveries(campaign_id, fixture, count=1, *, create_campaign=True, delivery_id=None):
    campaign_id = UUID(campaign_id)
    delivery = f"'{UUID(delivery_id)}'" if delivery_id else "gen_random_uuid()"
    assert fixture in {"w22", "tail", "late", "shutdown", "probe"}
    assert isinstance(count, int) and count > 0 and (delivery_id is None or count == 1)
    # AD-5: the fixture starts InProgress (1); each new delivery is Waiting (0).
    start = f"""
    INSERT INTO campaigns (id, name, message_text, status, created_at, updated_at, started_at)
    VALUES ('{campaign_id}', '{fixture}', 'synthetic', 1, now(), now(), now());
    INSERT INTO campaign_channels (campaign_id, channel) VALUES ('{campaign_id}', 'email');
    """ if create_campaign else ""
    return f"""
    {start}
    WITH seed AS (
        SELECT gen_random_uuid() AS id, '{fixture}-{campaign_id}-' || n || '@example.test' AS address
        FROM generate_series(1, {count}) n
    ), created AS (
        INSERT INTO contacts (id, created_at, updated_at)
        SELECT id, now(), now() FROM seed RETURNING id
    ), email AS (
        INSERT INTO contact_channels (id, contact_id, channel, address)
        SELECT id, id, 'email', address FROM seed JOIN created USING (id)
        RETURNING contact_id, channel, address
    ), selected AS (
        INSERT INTO campaign_contacts (campaign_id, contact_id)
        SELECT '{campaign_id}', contact_id FROM email RETURNING campaign_id, contact_id
    )
    INSERT INTO deliveries (id, campaign_id, contact_id, channel, address_snapshot,
                            delivery_status, created_at, updated_at)
    SELECT {delivery}, campaign_id, contact_id, channel, address, 0, now(), now()
    FROM selected JOIN email USING (contact_id);
    """
