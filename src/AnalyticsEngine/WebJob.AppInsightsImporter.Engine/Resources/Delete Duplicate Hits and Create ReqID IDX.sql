
-- Delete duplicates

	SET QUOTED_IDENTIFIER ON

	DECLARE @allHitsCursor as CURSOR;

	DECLARE @id as int;
	DECLARE @page_request_id as uniqueidentifier;
	DECLARE @lastPage_request_id as uniqueidentifier;
	DECLARE @duplicateCount as int;
	declare @rowCount as int
	set @rowCount= 0

	SET @allHitsCursor = CURSOR FOR
		SELECT
		y.id, y.page_request_id, dt.CountOf
		FROM hits y
			INNER JOIN (SELECT
							page_request_id, COUNT(*) AS CountOf
							FROM hits
							GROUP BY page_request_id
							HAVING COUNT(*)>1
						) dt ON y.page_request_id=dt.page_request_id
						order by dt.page_request_id
					
		open @allHitsCursor

		FETCH NEXT FROM @allHitsCursor INTO @id, @page_request_id, @duplicateCount
	
		WHILE @@FETCH_STATUS = 0
		BEGIN

			--Is this the 1st hit that's duplicate?
			if @lastPage_request_id = @page_request_id begin

				-- This isn't the 1st duplicate then
				print 'Deleting duplicate ' + convert(varchar, @id)
				delete from hits where id = @id

			end
			else begin
				print 'Skipping 1st dupliate hit ' + convert(varchar, @id)
				set @lastPage_request_id = @page_request_id 
			end
			-- Get next hit with duplicates
			FETCH NEXT FROM @allHitsCursor INTO @id, @page_request_id, @duplicateCount

		END

		Close @allHitsCursor
		DEALLOCATE @allHitsCursor


	-- One last count
		SELECT
		y.*, y.page_request_id, dt.CountOf
		FROM hits y
			INNER JOIN (SELECT
							page_request_id, COUNT(*) AS CountOf
							FROM hits
							GROUP BY page_request_id
							HAVING COUNT(*)>1
						) dt ON y.page_request_id=dt.page_request_id
						order by dt.page_request_id


-- Create pageRequest index if not exists, now we have no duplicates
if not exists (
SELECT * 
FROM sys.indexes 
WHERE name='IX_PageRequestID' AND object_id = OBJECT_ID('dbo.hits')
)
begin
	CREATE UNIQUE NONCLUSTERED INDEX [IX_PageRequestID] ON [dbo].[hits]
	(
		[page_request_id] ASC
	)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)
end

