
DECLARE @HitUpdatesCursor as CURSOR;

DECLARE @page_request_id as uniqueidentifier
DECLARE @seconds_on_page as float;

SET @HitUpdatesCursor = CURSOR FOR
	select 
		updates.[page_request_id],
		updates.seconds_on_page
	from ${STAGING_TABLE_UPDATES} updates

	open @HitUpdatesCursor

	FETCH NEXT FROM @HitUpdatesCursor INTO 
		@page_request_id, 
		@seconds_on_page;


	WHILE @@FETCH_STATUS = 0
BEGIN

	--print 'Updating ' + convert(varchar(250), @page_request_id)

	update hits 
		set seconds_on_page = @seconds_on_page
	where 
		page_request_id = @page_request_id


	FETCH NEXT FROM @HitUpdatesCursor INTO @page_request_id, @seconds_on_page;
end
