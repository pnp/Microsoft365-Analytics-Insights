
-- Insert missing lookups
INSERT INTO urls (full_url)
	SELECT distinct [url]
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join urls on urls.full_url = imports.url
	where full_url is null;

INSERT INTO page_titles (title)
	SELECT distinct imports.title 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join page_titles on page_titles.title = imports.title
	where page_titles.title is null and imports.title is not null;

INSERT INTO users(user_name)
	SELECT distinct imports.user_name 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join users on users.user_name = imports.user_name
	where users.user_name is null;

INSERT INTO sites(url_base)
	SELECT distinct imports.site_url 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join sites on sites.url_base = imports.site_url
	where sites.url_base is null and imports.site_url is not null and imports.site_url != '';

INSERT INTO webs(url_base, site_id, title)
	SELECT distinct imports.web_url, sites.id, 
		(select top 1 [${STAGING_TABLE_HIT_IMPORTS}].web_title from [${STAGING_TABLE_HIT_IMPORTS}] 
			where [${STAGING_TABLE_HIT_IMPORTS}].site_url = imports.web_url) as web_title		-- get 1st web title (for MUI sites)
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	inner join sites on sites.url_base = imports.site_url
	left join webs on webs.url_base = imports.web_url
	where webs.url_base is null and imports.site_url is not null and imports.site_url != '';


INSERT INTO [sessions] (ai_session_id, user_id)
	select distinct sessionsToImport.ai_session_id, users.id from [${STAGING_TABLE_HIT_IMPORTS}]
	inner join 
	(
		-- Select distinct sessions
		SELECT distinct imports.ai_session_id
		FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
		left join [sessions] on [sessions].ai_session_id = imports.ai_session_id
		where [sessions].ai_session_id is null
	) sessionsToImport 
		on sessionsToImport.ai_session_id = [${STAGING_TABLE_HIT_IMPORTS}].ai_session_id
	inner join users 
		on users.user_name = [${STAGING_TABLE_HIT_IMPORTS}].[user_name]
	;

INSERT INTO operating_systems(os_name)
	SELECT distinct imports.os_name 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join operating_systems on operating_systems.os_name = imports.os_name
	where operating_systems.os_name is null and imports.os_name is not null;

INSERT INTO devices(device_name)
	SELECT distinct imports.device_name 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join devices on devices.device_name = imports.device_name
	where devices.device_name is null and imports.device_name is not null;

INSERT INTO browsers(browser_name)
	SELECT distinct imports.browser_name 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join browsers on browsers.browser_name = imports.browser_name
	where browsers.browser_name is null and imports.browser_name is not null;

INSERT INTO countries(country_name)
	SELECT distinct imports.country_name 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join countries on countries.country_name = imports.country_name
	where countries.country_name is null and imports.country_name is not null;

INSERT INTO cities(city_name)
	SELECT distinct imports.city_name 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join cities on cities.city_name = imports.city_name
	where cities.city_name is null and imports.city_name is not null;

INSERT INTO provinces(province_name)
	SELECT distinct imports.province_name 
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
	left join provinces on provinces.province_name = imports.province_name
	where provinces.province_name is null and imports.province_name is not null;

--sp_updatestats

-- Insert hits from staging
insert into hits (
		url_id, 
		hit_timestamp, 
		location_province_id, 
		agent_id, 
		session_id, 
		page_title_id,
		device_id,
		os_id,
		city_id,
		country_id,
		page_request_id,
		sp_request_duration,
		page_load_time,
		web_id
	)

	select distinct 
		urls.id as urlId, 
		imports.[hit_timestamp], 
		provinces.id as provinceId, 
		browsers.id as browserId, 
		[sessions].id as sessionId, 
		page_titles.id as pageTitleId,
		devices.id as deviceId,
		operating_systems.id as osId,
		cities.id as cityId,
		countries.id as countryId,
		imports.[page_request_id],
		imports.[sp_request_duration],
		imports.[page_load_time],
		webs.id as webId
	FROM [${STAGING_TABLE_HIT_IMPORTS}] imports
		inner join urls on urls.full_url = imports.url
		inner join browsers on browsers.browser_name = imports.browser_name
		inner join users on users.user_name = imports.user_name
		inner join [sessions] on [sessions].ai_session_id = imports.ai_session_id and [sessions].user_id = users.id
		left join page_titles on page_titles.title = imports.title
		inner join devices on devices.device_name = imports.device_name
		inner join operating_systems on operating_systems.os_name = imports.os_name
		left join cities on cities.city_name = imports.city_name
		left join countries on countries.country_name = imports.country_name
		left join webs on webs.url_base = imports.web_url
		left join provinces on provinces.province_name = imports.province_name
		left join hits duplicatePageRequestHits on  duplicatePageRequestHits.page_request_id = imports.page_request_id
	where duplicatePageRequestHits.page_request_id is null
