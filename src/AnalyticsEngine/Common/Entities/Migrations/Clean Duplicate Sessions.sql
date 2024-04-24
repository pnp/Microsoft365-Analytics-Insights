
	SET QUOTED_IDENTIFIER ON

	-- Updates hits from session duplicates #2+ to use 1st session ID

	DECLARE @allSessionsCursor as CURSOR;

	DECLARE @id as int;
	DECLARE @ai_session_id as varchar(50);
	DECLARE @duplicateCount as int;
	declare @rowCount as int
	set @rowCount= 0

	SET @allSessionsCursor = CURSOR FOR
		SELECT
		y.id,y.ai_session_id, dt.CountOf
		FROM [sessions] y
			INNER JOIN (SELECT
							ai_session_id, COUNT(*) AS CountOf
							FROM [sessions]
							GROUP BY ai_session_id
							HAVING COUNT(*)>1
						) dt ON y.ai_session_id=dt.ai_session_id
						order by dt.ai_session_id
					
		open @allSessionsCursor

		FETCH NEXT FROM @allSessionsCursor INTO @id, @ai_session_id, @duplicateCount
	
		WHILE @@FETCH_STATUS = 0
		BEGIN

		DECLARE @duplicateSessionsCursor as CURSOR;

		DECLARE @dup_id as int;
		DECLARE @dup_ai_session_id as varchar(50);

		declare @duplicateRowCount as int
		set @duplicateRowCount= 0
		
		SET @duplicateSessionsCursor = CURSOR FOR
				select [sessions].id, [sessions].ai_session_id
				from [sessions]
				where [sessions].ai_session_id = @ai_session_id
					
		open @duplicateSessionsCursor

		declare @firstSessionId int
		
		-- Fetch 1st
		FETCH NEXT FROM @duplicateSessionsCursor INTO @dup_id, @dup_ai_session_id;
				
		-- Loop inner duplicate check
		WHILE @@FETCH_STATUS = 0 BEGIN
			
			-- Duplicate logic
			if @duplicateRowCount = 0 begin
				-- Remember 1st result
				set @firstSessionId = @dup_id
			end

			if @duplicateRowCount > 0 begin

				print '"' +@ai_session_id + '" has ' + Convert(VARCHAR, @duplicateRowCount) + ' duplicates. Updating hits to use 1st session ID ' + Convert(VARCHAR, @firstSessionId)

				-- Update this session with 1st session
				update hits set session_id = @firstSessionId where session_id = @dup_id

				delete from [sessions] where id = @dup_id

			end

			set @duplicateRowCount = @duplicateRowCount +1

			FETCH NEXT FROM @duplicateSessionsCursor INTO 
				@dup_id, 
				@dup_ai_session_id;
	
				-- duplicates here
		end

		CLOSE @duplicateSessionsCursor
		DEALLOCATE @duplicateSessionsCursor

		if @rowCount % 100 = 0 begin
			print ' Processed ' + Convert(VARCHAR, @rowCount)
		end
		set @rowCount = @rowCount +1

		-- Read next event
				
	FETCH NEXT FROM @allSessionsCursor INTO @id, @ai_session_id, @duplicateCount

	END

	Close @allSessionsCursor
	DEALLOCATE @allSessionsCursor


	-- One last count
	SELECT
    y.id,y.ai_session_id, dt.CountOf
    FROM [sessions] y
        INNER JOIN (SELECT
                        ai_session_id, COUNT(*) AS CountOf
                        FROM [sessions]
                        GROUP BY ai_session_id
                        HAVING COUNT(*)>1
                    ) dt ON y.ai_session_id=dt.ai_session_id
					order by dt.ai_session_id