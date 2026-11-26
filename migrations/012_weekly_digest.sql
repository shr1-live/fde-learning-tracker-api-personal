-- A Monday summary for the admin: what was handed in, approved and logged last week. One row per admin per Monday.
-- Idempotent: safe to run more than once.
alter table tracker_notifications drop constraint if exists tracker_notifications_kind_check;
alter table tracker_notifications add constraint tracker_notifications_kind_check check (kind in ('daily_log_missed', 'week_submitted', 'weekly_digest'));
