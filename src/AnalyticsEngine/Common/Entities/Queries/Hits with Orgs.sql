DECLARE @hitUrlsCursor as CURSOR;
DECLARE @OrgsCursor as CURSOR;
DECLARE @hitUrl as NVARCHAR(2500);
DECLARE @hitUrlID as int;
DECLARE @orgUrl as NVARCHAR(2500);
DECLARE @orgID as int;

DECLARE @longestOrgUrlLen as int;
DECLARE @selectedOrgId as int;

SET @hitUrlsCursor = CURSOR FOR
       select urls.id, full_url from urls

       open @hitUrlsCursor

       -- Create temp table
       IF OBJECT_ID('tempdb..#tmpHits') IS NOT NULL DROP TABLE #tmpHits

       CREATE TABLE #tmpHits(
              [id] [int] NOT NULL,
              [url_id] [int] NOT NULL,
              [hit_timestamp] [datetime] NOT NULL,
              [location_province_id] [int] NULL,
              [agent_id] [int] NULL,
              [session_id] [int] NULL,
              [page_title_id] [int] NULL,
              [time_on_page] [float] NULL, 
              [hit_org_id] [int] NULL
       )

       FETCH NEXT FROM @hitUrlsCursor INTO @hitUrlID, @hitUrl;

WHILE @@FETCH_STATUS = 0
BEGIN

       -- Reset vars
       set @selectedOrgId = 0
       set @longestOrgUrlLen = 0

       -- Find org that has this URL
       SET @OrgsCursor = CURSOR FOR
              select orgs.id, org_urls.url_base from orgs
              inner join org_urls on org_urls.org_id = orgs.id
       open @OrgsCursor

       -- read 1st org in loop
       FETCH NEXT FROM @OrgsCursor INTO @orgID, @orgUrl;
       WHILE @@FETCH_STATUS = 0
       BEGIN

              -- Does the hit URL contain the org URL?
              if CHARINDEX(@orgUrl, @hitUrl) > 0
              begin

                     print 'Found ' + @orgUrl + ' in ' + @hitUrl

                     if len(@orgUrl) > @longestOrgUrlLen
                     begin
                           Set @longestOrgUrlLen = len(@orgUrl)
                           set @selectedOrgId = @orgID
                           print 'Found org match. New length: ' + CONVERT(varchar(100), len(@orgUrl))  + ''
                     end
                     else
                     begin
                           print 'Found match but not long enough'
                     end
                     
              end
              else
              begin
                     print @orgUrl + ' not found in hit ' + @hitUrl
              end 

              -- Read next org URL
              FETCH NEXT FROM @OrgsCursor INTO @orgID, @orgUrl;

       END
       DEALLOCATE @OrgsCursor

       -- Insert all hits for this URL into temp table
       insert into #tmpHits 
              ([id], [url_id], [hit_timestamp], [location_province_id], [agent_id], [session_id], [page_title_id], [time_on_page], [hit_org_id])
              SELECT [id], [url_id], [hit_timestamp], [location_province_id], [agent_id], [session_id], [page_title_id], [time_on_page], @selectedOrgId
                     FROM hits 
                     where hits.url_id = @hitUrlID


       FETCH NEXT FROM @hitUrlsCursor INTO @hitUrlID, @hitUrl;

END


DEALLOCATE @hitUrlsCursor

select * from #tmpHits
