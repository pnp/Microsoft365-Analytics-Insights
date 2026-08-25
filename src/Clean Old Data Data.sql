declare @archiveDateMax datetime

--Archive date: one month before "now". All records will use this value to delete from
set @archiveDateMax = dateadd(month, -1, GETDATE())

--IMPORTANT: strongly recommend you run this in a transaction first and rollback to test

--begin transaction archive

--Delete hits & activity from before achive date
delete from hits where [hit_timestamp] < @archiveDateMax

-- Delete searches that have sessions that have now no hits
delete from searches where exists (
	select id from [sessions] s where not exists (select * from hits where hits.session_id = s.id)
		and id = searches.session_id
)

--Delete sessions that have no hits
declare @count int
set @count = (select count(*) from sessions)
print 'session count before session clean: ' + cast(@count as nvarchar)
delete s from [sessions] s where not exists (select * from hits where hits.session_id = s.id)
set @count = (select count(*) from sessions)
print 'session count after session clean: ' + cast(@count as nvarchar)

-- Azure AD event props
delete audit_event_azure_ad_props from audit_event_azure_ad_props
	inner join event_meta_azure_ad on event_meta_azure_ad.event_id = audit_event_azure_ad_props.event_id
		inner join audit_events on audit_events.id = event_meta_azure_ad.event_id
			where audit_events.[time_stamp] < @archiveDateMax
-- Azure AD
delete event_meta_azure_ad from event_meta_azure_ad
	inner join audit_events on audit_events.id = event_meta_azure_ad.event_id
	where audit_events.[time_stamp] < @archiveDateMax

-- Exchange event props
delete audit_event_exchange_props from audit_event_exchange_props
	inner join event_meta_exchange on event_meta_exchange.event_id = audit_event_exchange_props.event_id
		inner join audit_events on audit_events.id = event_meta_exchange.event_id
			where audit_events.[time_stamp] < @archiveDateMax
-- Exchange
delete event_meta_exchange from event_meta_exchange
	inner join audit_events on audit_events.id = event_meta_exchange.event_id
	where audit_events.[time_stamp] < @archiveDateMax

-- SharePoint
delete [event_meta_sharepoint] from [event_meta_sharepoint]
	inner join audit_events on audit_events.id = [event_meta_sharepoint].event_id
	where audit_events.[time_stamp] < @archiveDateMax

-- General events
delete [event_meta_general] from [event_meta_general]
	inner join audit_events on audit_events.id = [event_meta_general].event_id
	where audit_events.[time_stamp] < @archiveDateMax


-- Teams
-- Deprecated Teams add-on tracking: these tables are no longer written to and are dropped by the
-- DeprecateTeamsAddons migration once empty, so only prune them when they are still present.
-- Dynamic SQL keeps this batch parseable on a database where they have already gone.
IF OBJECT_ID('dbo.teams_addons_log', 'U') IS NOT NULL
	EXEC sp_executesql N'delete from teams_addons_log where [date] < @archiveDateMax', N'@archiveDateMax datetime', @archiveDateMax = @archiveDateMax
IF OBJECT_ID('dbo.teams_addons_user_installed_log', 'U') IS NOT NULL
	EXEC sp_executesql N'delete from teams_addons_user_installed_log where [date] < @archiveDateMax', N'@archiveDateMax datetime', @archiveDateMax = @archiveDateMax
delete teams_channel_stats_log_keywords from teams_channel_stats_log_keywords 
	inner join teams_channel_stats_log on teams_channel_stats_log.id = teams_channel_stats_log_keywords.channel_stats_log_id
	where teams_channel_stats_log.[date] < @archiveDateMax 
delete teams_channel_stats_log_langs from teams_channel_stats_log_langs 
	inner join teams_channel_stats_log on teams_channel_stats_log.id = teams_channel_stats_log_langs.channel_stats_log_id
	where teams_channel_stats_log.[date] < @archiveDateMax 

delete from teams_channel_stats_log where [date] < @archiveDateMax 
delete from teams_channel_tabs_log where [date] < @archiveDateMax 
delete from team_membership_log where [date] < @archiveDateMax 


--Activity clean-up
delete from onedrive_usage_activity_log where [date] < @archiveDateMax
delete from onedrive_user_activity_log where [date] < @archiveDateMax
delete from outlook_user_activity_log where [date] < @archiveDateMax
delete from sharepoint_user_activity_log where [date] < @archiveDateMax
delete from teams_user_activity_log where [date] < @archiveDateMax
delete from yammer_device_activity_log where [date] < @archiveDateMax
delete from yammer_group_activity_log where [date] < @archiveDateMax
delete from yammer_user_activity_log where [date] < @archiveDateMax

-- Copilot AI interaction history (optional import).
--
-- Retention matters more here than anywhere else in the schema: these rows describe how individual
-- people used Copilot, turn by turn. No prompt or response text is ever stored - only counts, plus a
-- sentiment score and key phrases when cognitive services are enabled - but the rows are still
-- personal usage data and should not be kept indefinitely.
--
-- Guarded with OBJECT_ID because the tables only exist once the AddCopilotInteractionHistory migration
-- has been applied, and this script is run against databases at a range of versions.
-- Leaf -> parent order: key phrases, interactions, then the sessions left with nothing in them.
if OBJECT_ID('dbo.copilot_interaction_keywords', 'U') is not null
	delete k from copilot_interaction_keywords k
		inner join copilot_interactions i on i.id = k.interaction_id
		where i.created_utc < @archiveDateMax

if OBJECT_ID('dbo.copilot_interactions', 'U') is not null
	delete from copilot_interactions where created_utc < @archiveDateMax

-- Sessions are only removed once every interaction in them has aged out, so a long-running
-- conversation that is still partly inside the retention window keeps its thread.
if OBJECT_ID('dbo.copilot_interaction_sessions', 'U') is not null
	delete s from copilot_interaction_sessions s
		where not exists (select 1 from copilot_interactions i where i.session_id = s.id)

-- Import run diagnostics are pure operational telemetry.
if OBJECT_ID('dbo.copilot_interaction_import_log', 'U') is not null
	delete from copilot_interaction_import_log where run_started_utc < @archiveDateMax

-- Extracted key phrases can be a whole short prompt, so a phrase left behind after its interaction has
-- aged out would outlive the data it came from. Remove any keyword no longer referenced by anything.
-- The keywords table is shared with Teams channel analysis, so both referencing tables are checked.
if OBJECT_ID('dbo.copilot_interaction_keywords', 'U') is not null
	delete k from keywords k
		where not exists (select 1 from copilot_interaction_keywords ck where ck.keyword_id = k.id)
		  and not exists (select 1 from teams_channel_stats_log_keywords tk where tk.keyword_id = k.id)

-- NB: copilot_interaction_user_watermarks is deliberately NOT cleaned here. It holds no interaction
-- data (just "how far did we get for this user"), and deleting it would make the next import re-scan
-- each user's whole backfill window - one Graph call per user, which is the exact cost this feature
-- is designed to avoid.


-- Copilot usage reports (optional import) - issue #286.
--
-- These are Microsoft's own per-user Copilot figures, one row per (date, user, report period). The
-- import refreshes them daily and keeps every historical snapshot, so the table grows by roughly
-- (licensed users x report periods) rows per day forever. On a large tenant that is the
-- fastest-growing table this feature adds, and it had no retention bound at all - which is how the
-- deprecated Teams add-on tables got so expensive.
--
-- Bounded on [date] (the report's own snapshot date), matching how every other daily log here is aged.
-- Guarded with OBJECT_ID because the tables only exist once the AddCopilotUsageReports migration has
-- run, and this script is also used against older databases.
--
-- DELETED IN BATCHES, unlike the older statements above. At the 200,000-user baseline this table
-- gains ~800,000 rows a day (users x 4 report periods), so the FIRST run after upgrading has to
-- remove everything older than a month that has accumulated since the import was enabled - which can
-- be tens of millions of rows. A single DELETE of that size takes one long exclusive lock, escalates
-- to a table lock, and writes the whole thing to the log as one transaction. Batching keeps each
-- statement short so the purge can run alongside an importer rather than blocking it.
--
-- NB: batching only bounds the transaction if the script is NOT wrapped in the "begin transaction
-- archive" above. Inside that transaction the locks and log growth accumulate regardless; the batch
-- loop then only limits how much each individual statement scans.
declare @copilotBatch int = 10000
declare @copilotDeleted int

if OBJECT_ID('dbo.copilot_usage_user_activity_log', 'U') is not null
begin
	set @copilotDeleted = @copilotBatch
	while @copilotDeleted = @copilotBatch
	begin
		delete top (10000) from copilot_usage_user_activity_log where [date] < @archiveDateMax
		set @copilotDeleted = @@ROWCOUNT
	end
end

if OBJECT_ID('dbo.copilot_user_count_log', 'U') is not null
begin
	set @copilotDeleted = @copilotBatch
	while @copilotDeleted = @copilotBatch
	begin
		delete top (10000) from copilot_user_count_log where report_date < @archiveDateMax
		set @copilotDeleted = @@ROWCOUNT
	end
end

-- The per-run diagnostics log, aged like the interaction-history one above.
if OBJECT_ID('dbo.copilot_usage_report_import_log', 'U') is not null
begin
	set @copilotDeleted = @copilotBatch
	while @copilotDeleted = @copilotBatch
	begin
		delete top (10000) from copilot_usage_report_import_log where imported_utc < @archiveDateMax
		set @copilotDeleted = @@ROWCOUNT
	end
end

-- commit/rollback
--rollback transaction archive

--commit transaction archive
