-- Aggregate proof that contacts, campaign selections and delivery snapshots agree.
SELECT json_build_object(
    'contacts', (SELECT count(*) FROM contacts),
    'channels', (SELECT count(*) FROM contact_channels),
    'distinct_addresses', (SELECT count(DISTINCT address) FROM contact_channels),
    'campaign_contacts', (SELECT count(*) FROM campaign_contacts WHERE campaign_id=:'cid'::uuid),
    'campaign_channels', (SELECT count(*) FROM campaign_channels WHERE campaign_id=:'cid'::uuid),
    'matching_deliveries', (
        SELECT count(*) FROM deliveries d
        JOIN contact_channels ch ON ch.contact_id=d.contact_id AND ch.channel=d.channel
        JOIN campaign_contacts cc ON cc.campaign_id=d.campaign_id AND cc.contact_id=d.contact_id
        JOIN campaign_channels cs ON cs.campaign_id=d.campaign_id AND cs.channel=d.channel
        WHERE d.campaign_id=:'cid'::uuid AND d.channel='email'
          AND d.address_snapshot=ch.address AND d.address_snapshot !~ '^[[:space:]]*$'
    )
);
