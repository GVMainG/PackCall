-- psql variable campaign_id must be a UUID. No individual delivery identifiers leave this query.
BEGIN READ ONLY;
SET LOCAL statement_timeout = '500ms';
SELECT json_build_object(
    'observed_repeat_captures', COALESCE(sum(greatest(captures - 1, 0)), 0),
    'journal_non_atomic', true,
    'quality', 'lower_bound'
)
FROM (
    SELECT count(*) AS captures
    FROM delivery_status_journal
    WHERE campaign_id = :'campaign_id'::uuid AND previous_status = 0 AND new_status = 1
    GROUP BY delivery_id
) AS per_delivery;
COMMIT;
