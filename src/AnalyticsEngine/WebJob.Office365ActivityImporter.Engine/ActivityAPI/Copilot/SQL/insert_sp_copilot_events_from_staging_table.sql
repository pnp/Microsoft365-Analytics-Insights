INSERT INTO urls(full_url)
	SELECT distinct imports.url 
	FROM ${STAGING_TABLE_ACTIVITY} imports
	left join 
		urls on urls.full_url = imports.url
	where 
		not exists(select top 1 full_url from urls where full_url = imports.url)


INSERT INTO sites(url_base)
	SELECT distinct imports.url_base 
	FROM ${STAGING_TABLE_ACTIVITY} imports
	left join 
		sites on sites.url_base = imports.url_base
	where 
		imports.url_base is not null AND 
		not exists(select top 1 url_base from sites where url_base = imports.url_base)

		
INSERT INTO event_file_ext(extension_name)
	SELECT distinct imports.file_extension 
	FROM ${STAGING_TABLE_ACTIVITY} imports
	left join event_file_ext on event_file_ext.extension_name = imports.file_extension
	where event_file_ext.extension_name is null and imports.file_extension is not null and imports.file_extension != '';

INSERT INTO event_file_names(file_name)
	SELECT distinct imports.file_name 
	FROM ${STAGING_TABLE_ACTIVITY} imports
	left join event_file_names on event_file_names.file_name = imports.file_name
	where event_file_names.file_name is null and imports.file_name is not null and imports.file_name != '';

insert into [event_copilot_chats] (event_id, app_host)
	SELECT imports.event_id,app_host FROM ${STAGING_TABLE_ACTIVITY} imports

insert into [event_copilot_files] (copilot_chat_id, file_name_id, file_extension_id, url_id, site_id)
	SELECT imports.event_id
		  ,event_file_names.id as fileNameId
		  ,event_file_ext.id as fileExtId
		  ,urls.id as urlId
		  ,sites.id as siteId
	  FROM ${STAGING_TABLE_ACTIVITY} imports
	  inner join event_file_names on event_file_names.file_name = imports.file_name
	  inner join event_file_ext on event_file_ext.extension_name = imports.file_extension
	  inner join urls on urls.full_url = imports.url
	  inner join sites on sites.url_base = imports.url_base
