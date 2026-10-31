SELECT json_build_object(
 'db_now', statement_timestamp(),
 'waiting_total', count(*) FILTER (WHERE d.delivery_status=0),
 'waiting_eligible', count(*) FILTER (WHERE d.delivery_status=0 AND c.status=1),
 'in_progress', count(*) FILTER (WHERE d.delivery_status=1),
 'expired', count(*) FILTER (WHERE d.delivery_status=1 AND d.lock_expires_at<=statement_timestamp()),
 'sent', count(*) FILTER (WHERE d.delivery_status=2),
 'failed', count(*) FILTER (WHERE d.delivery_status=-1),
 'oldest_waiting_age_s', extract(epoch FROM statement_timestamp()-min(d.created_at) FILTER (WHERE d.delivery_status=0)),
 'oldest_eligible_age_s', extract(epoch FROM statement_timestamp()-min(d.created_at) FILTER (WHERE d.delivery_status=0 AND c.status=1))
)
FROM deliveries d JOIN campaigns c ON c.id=d.campaign_id
WHERE d.campaign_id=:'campaign_id'::uuid;
