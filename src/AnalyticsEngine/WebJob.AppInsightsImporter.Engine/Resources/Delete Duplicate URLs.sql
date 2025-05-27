
-- Delete duplicate URLs. 
-- For URL lookups that have +1 same URL string, we migrate all lookups to the 1st found result, then delete the old lookup.
-- RECOMMENDED: For directly running against a SQL database, try in a transaction and rollback 1st.
-- Tests: src\AnalyticsEngine\Tests.UnitTests\DuplicateUrlTests.cs - ensures the right data is deleted by the below T-SQL

	SET QUOTED_IDENTIFIER ON

	DECLARE @allUrlsCursor as CURSOR;

	DECLARE @id as int;
	DECLARE @skipped_url_id as int;
	DECLARE @last_url as varchar(max);
	DECLARE @full_url as varchar(max);
	DECLARE @duplicateCount as int;
	declare @rowCount as int
	set @rowCount= 0
	set @skipped_url_id = 0
	set @last_url = ''

	SET @allUrlsCursor = CURSOR FOR
		SELECT
		y.id, y.full_url, dt.CountOf
		FROM urls y
			INNER JOIN (SELECT
							full_url, COUNT(*) AS CountOf
							FROM urls
							GROUP BY full_url
							HAVING COUNT(*)>1
						) dt ON y.full_url=dt.full_url
						order by dt.CountOf
					
		open @allUrlsCursor

		FETCH NEXT FROM @allUrlsCursor INTO @id, @full_url, @duplicateCount
	
		WHILE @@FETCH_STATUS = 0
		BEGIN

			--Is this the 1st hit that's duplicate?
			if @last_url = @full_url begin

				print 'Migrating lookups for url ' + @full_url + ' to new id ' + convert(varchar, @skipped_url_id)

				--These are the dependant lookups for URL table. Move them to the 1st result for duplicate URLs
				update hits set url_id = @skipped_url_id where url_id = @id
				update event_copilot_files set url_id = @skipped_url_id where url_id = @id
				update event_meta_sharepoint set url_id = @skipped_url_id where url_id = @id
				update page_likes set url_id = @skipped_url_id where url_id = @id
				update page_comments set url_id = @skipped_url_id where url_id = @id
				update file_metadata_property_values set url_id = @skipped_url_id where url_id = @id

				
				-- This isn't the 1st duplicate then
				print 'Deleting duplicate url ' + convert(varchar, @id)

				
				delete from urls where id = @id

			end
			else begin
				print 'Skipping 1st duplicate url ' + @full_url + ' with id ' + convert(varchar, @id)
				set @last_url = @full_url
				set @skipped_url_id = @id		-- If the next URL is the same as this one, we'll move everything to this ID
			end

			-- Get next hit with duplicates
			FETCH NEXT FROM @allUrlsCursor INTO @id, @full_url, @duplicateCount

		END

		Close @allUrlsCursor
		DEALLOCATE @allUrlsCursor

