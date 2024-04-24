
	SET QUOTED_IDENTIFIER ON

	DECLARE @allUrlsCursor as CURSOR;

	DECLARE @id as int;
	DECLARE @full_url as nvarchar(max);
	DECLARE @duplicateCount as int;

	
	DECLARE @lastId as int;

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
						order by dt.full_url
					
		open @allUrlsCursor


		
		FETCH NEXT FROM @allUrlsCursor INTO @id, @full_url, @duplicateCount
	
		WHILE @@FETCH_STATUS = 0
		BEGIN

			--Is this the 1st url that's duplicate?
			if @lastId = @id begin

				-- This isn't the 1st duplicate then
				print 'Deleting duplicate ' + convert(varchar, @id)
				delete from urls where id = @id

			end
			else begin
				print 'Skipping 1st dupliate ' + convert(varchar, @id)
				set @lastId = @id
			end
			-- Get next hit with duplicates
			FETCH NEXT FROM @allUrlsCursor INTO @id, @full_url, @duplicateCount

		END

		Close @allUrlsCursor
		DEALLOCATE @allUrlsCursor
