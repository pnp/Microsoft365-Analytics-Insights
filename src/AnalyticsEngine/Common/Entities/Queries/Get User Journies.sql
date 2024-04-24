
DECLARE @HitsCursor as CURSOR;
DECLARE @page1 as NVARCHAR(500);
DECLARE @page2 as NVARCHAR(500);
DECLARE @hitSessionID as int;
DECLARE @nextHitSessionID as int;
DECLARE @hitID as int;
DECLARE @nextHitID as int;
DECLARE @hitTimeStamp as datetime;
DECLARE @nextHitTimeStamp as datetime;

SET @HitsCursor = CURSOR FOR
	select hits.id, title, session_id, hit_timestamp from hits 
	inner join urls on hits.url_id = urls.id
	inner join [sessions] on hits.session_id = [sessions].id
	inner join [users] on [sessions].user_id = users.id
	inner join page_titles on hits.page_title_id = page_titles.id
	order by session_id, hit_timestamp

	open @HitsCursor

	-- Create temp table
	IF OBJECT_ID('tempdb..#tmp') IS NOT NULL DROP TABLE #tmp
	create table #tmp (id int, start_page NVARCHAR(500), end_page NVARCHAR(500), session_id int, time_stamp datetime)
	
	FETCH NEXT FROM @HitsCursor INTO @hitID, @page1, @hitSessionID, @hitTimeStamp;
	FETCH NEXT FROM @HitsCursor INTO @nextHitID, @page2, @nextHitSessionID, @nextHitTimeStamp;

	insert into #tmp values (@hitID, @page1, @page2, @hitSessionID, @hitTimeStamp)
	set @hitID = @nextHitID

	WHILE @@FETCH_STATUS = 0
BEGIN

		/* old end_page will be next insert start_page; next read will be new end_page */
		set @page1 = @page2

		-- Read next hit
 		FETCH NEXT FROM @HitsCursor INTO @nextHitID, @page2, @nextHitSessionID, @nextHitTimeStamp;

		if @@FETCH_STATUS = 0 begin
			if @nextHitSessionID = @hitSessionID begin
				insert into #tmp values (@hitID, @page1, @page2, @hitSessionID, @hitTimeStamp)
			end

		end

		-- Set next insert for the hit read ahead of current iteration
		set @hitSessionID = @nextHitSessionID
		set @hitTimeStamp = @nextHitTimeStamp
		set @hitID = @nextHitID
END


DEALLOCATE @HitsCursor

select * from #tmp

