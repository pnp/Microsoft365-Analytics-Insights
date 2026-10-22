INSERT INTO urls(full_url)
	SELECT distinct imports.url 
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	left join 
		urls on urls.full_url = imports.url
	where 
		imports.url is not null AND
		not exists(select top 1 full_url from urls where full_url = imports.url)


INSERT INTO sites(url_base)
	SELECT distinct imports.url_base 
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	left join 
		sites on sites.url_base = imports.url_base
	where 
		imports.url_base is not null AND 
		not exists(select top 1 url_base from sites where url_base = imports.url_base)

		
-- event_file_ext.extension_name is nvarchar (migration EventFileExtensionUnicode), so the extension is stored
-- exactly as logged. CONVERT still trims it to the column's 250 characters, so an over-long one can't fail the
-- batch, and the insert, de-duplication and joins below all use that same trimmed value.
INSERT INTO event_file_ext(extension_name)
	SELECT distinct ext.extension_name
	FROM (
		SELECT CONVERT(nvarchar(250), imports.file_extension COLLATE DATABASE_DEFAULT) as extension_name
		FROM [${STAGING_TABLE_ACTIVITY}] imports
		where imports.file_extension is not null and imports.file_extension != ''
	) ext
	where not exists (select 1 from event_file_ext existing where existing.extension_name = ext.extension_name);

-- file_name is staged trimmed to event_file_names.file_name's nvarchar(250) without splitting a surrogate pair
-- (CopilotAuditEventManager). LEFT() is the backstop that stops an untrimmed name failing the whole batch.
INSERT INTO event_file_names(file_name)
	SELECT distinct LEFT(imports.file_name, 250)
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	where imports.file_name is not null and imports.file_name != ''
		and not exists (select 1 from event_file_names existing where existing.file_name = LEFT(imports.file_name, 250));


insert into [copilot_event_files] (copilot_chat_id, file_name_id, file_extension_id, url_id, site_id)
	SELECT imports.event_id
		,event_file_names.id as fileNameId
		,event_file_ext.id as fileExtId
		,urls.id as urlId
		,sites.id as siteId
	  FROM [${STAGING_TABLE_ACTIVITY}] imports
	  inner join event_file_names on event_file_names.file_name = LEFT(imports.file_name, 250)
	  inner join event_file_ext on event_file_ext.extension_name = CONVERT(nvarchar(250), imports.file_extension COLLATE DATABASE_DEFAULT)
	  inner join urls on urls.full_url = imports.url
	  inner join sites on sites.url_base = imports.url_base
	  where not exists (
		select 1
		from copilot_event_files existing
		where existing.copilot_chat_id = imports.event_id
	  )
