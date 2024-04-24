 
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

-- Calculate existing max/min date range
declare @existingMax datetime = isnull((select max(timestamp) from hits_clicked_elements), cast('1753-1-1' as datetime))

-- Insert clicks from staging
insert into hits_clicked_elements (
		url_id, 
		element_title_id,
		class_names_id,
		hit_id,
		timestamp
	)
	select distinct
		urls.id as urlId, 
		hits_clicked_element_titles.id as titleId,
		hits_clicked_element_class_names.id as classesId,
		hits.id as hit_id,
		imports.timestamp
	FROM ${STAGING_TABLE_CLICKS} imports
		left join urls 
			on urls.full_url = imports.url
		inner join hits_clicked_element_titles 
			on hits_clicked_element_titles.name = imports.link_text
		left join hits_clicked_element_class_names 
			on hits_clicked_element_class_names.class_names = imports.class_names
		inner join hits  
			on hits.page_request_id = imports.page_request_id
		where convert(varchar, imports.timestamp) != '0001-01-01 00:00:00.0000000'
			and (	-- Duplicates clicks check. Make sure it's older than the last existing click
				cast(imports.timestamp as datetime) > @existingMax
			)
