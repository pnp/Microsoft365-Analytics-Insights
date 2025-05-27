
-- Delete duplicates

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

				print 'Migrating lookups for url ' + @full_url
				update hits set url_id = @skipped_url_id where url_id = @id
				
				-- This isn't the 1st duplicate then
				print 'Deleting duplicate url ' + convert(varchar, @id)

				
				delete from urls where id = @id

			end
			else begin
				print 'Skipping 1st dupliate url ' + convert(varchar, @id)
				set @last_url = @full_url
				set @skipped_url_id = @id
			end
			-- Get next hit with duplicates
			FETCH NEXT FROM @allUrlsCursor INTO @id, @full_url, @duplicateCount

		END

		Close @allUrlsCursor
		DEALLOCATE @allUrlsCursor

