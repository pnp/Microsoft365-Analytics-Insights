begin transaction  


declare @DeleteID TABLE(id uniqueidentifier)

-- Find general events that aren't also SP events
insert into @DeleteID(id) (
	select [event_meta_general].event_id from [event_meta_general] 
	left join event_meta_sharepoint on event_meta_sharepoint.event_id = event_meta_general.event_id
	where ISNULL(event_meta_sharepoint.event_id, CAST(0x0 AS UNIQUEIDENTIFIER)) = CAST(0x0 AS UNIQUEIDENTIFIER)
	)

-- Delete
delete from [event_meta_general]
delete e from audit_events e inner join @DeleteID d on e.id = d.id 


rollback transaction
