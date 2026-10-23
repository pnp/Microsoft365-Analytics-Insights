 
-- Insert missing lookups

-- Most things are optional, except link title
INSERT INTO hits_clicked_element_class_names(class_names)
	SELECT distinct imports.class_names 
	FROM ${STAGING_TABLE_CLICKS} imports
	left join hits_clicked_element_class_names on hits_clicked_element_class_names.class_names = imports.class_names
	where imports.class_names is not null and hits_clicked_element_class_names.class_names is null;

INSERT INTO urls(full_url)
	SELECT distinct imports.url 
	FROM ${STAGING_TABLE_CLICKS} imports
	left join urls on urls.full_url = imports.url
	where imports.url is not null and urls.full_url is null;

INSERT INTO hits_clicked_element_titles(name)
	SELECT distinct imports.link_text 
	FROM ${STAGING_TABLE_CLICKS} imports
	left join hits_clicked_element_titles on hits_clicked_element_titles.name = imports.link_text
	where hits_clicked_element_titles.name is null;

-- Insert clicks from staging.
-- A click is identified by the key the unique index IX_hit_id_url_id_timestamp enforces - (hit_id, url_id,
-- timestamp), at the datetime precision the timestamp is stored with. Every cycle re-reads the whole day, so most
-- staged clicks are already stored, and NOT EXISTS against that index skips exactly those, however old they are.
-- This used to skip every click not newer than the newest click in the whole table. That time comes from the
-- browser's clock, so a click that reached App Insights after a later-stamped one was dropped for good, and one
-- browser with its clock ahead stopped all clicks in the tenant being imported until real time caught up.
-- ROW_NUMBER keeps one click per key within the batch (the lowest title id, then the lowest class names id), so
-- two staged clicks sharing a key but differing in title or class names - which SELECT DISTINCT kept apart - can
-- no longer fail the whole batch on the unique index.
;with clicksToImport as
(
	select
		urls.id as url_id,
		hits_clicked_element_titles.id as element_title_id,
		hits_clicked_element_class_names.id as class_names_id,
		hits.id as hit_id,
		clickTime.[timestamp],
		row_number() over (
			partition by hits.id, urls.id, clickTime.[timestamp]
			order by hits_clicked_element_titles.id, hits_clicked_element_class_names.id) as rowNumberPerClick
	FROM ${STAGING_TABLE_CLICKS} imports
		cross apply (select try_convert(datetime, imports.[timestamp]) as [timestamp]) clickTime
		left join urls 
			on urls.full_url = imports.url
		inner join hits_clicked_element_titles 
			on hits_clicked_element_titles.name = imports.link_text
		left join hits_clicked_element_class_names 
			on hits_clicked_element_class_names.class_names = imports.class_names
		inner join hits  
			on hits.page_request_id = imports.page_request_id
	-- A time outside the datetime range (e.g. an unset 0001-01-01) cannot be stored.
	where clickTime.[timestamp] is not null
)
insert into hits_clicked_elements (
		url_id, 
		element_title_id,
		class_names_id,
		hit_id,
		[timestamp]
	)
	select
		url_id,
		element_title_id,
		class_names_id,
		hit_id,
		[timestamp]
	from clicksToImport
	where rowNumberPerClick = 1
		and not exists
		(
			-- INTERSECT compares url_id NULL-safely, as the unique index does, and still seeks on the index.
			select 1 from hits_clicked_elements existing
			where existing.hit_id = clicksToImport.hit_id
				and exists (select existing.url_id, existing.[timestamp] intersect select clicksToImport.url_id, clicksToImport.[timestamp])
		);