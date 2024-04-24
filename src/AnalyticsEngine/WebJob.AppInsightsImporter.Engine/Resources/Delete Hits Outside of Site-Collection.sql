
begin transaction nonSiteCollectionHitsTrans

-- Delete Hits Outside of Site-Collection
	DECLARE @outOfScopeURLs as int;
	set @outOfScopeURLs = 
		(SELECT
			count(full_url)
		FROM hits 
			INNER JOIN urls on hits.url_id = urls.id
			inner join webs on webs.id = hits.web_id
			inner join sites on sites.id = webs.site_id
		where full_url not like (sites.url_base + '%')
				and full_url != sites.url_base)

	print 'Found ' + convert(nvarchar(255), @outOfScopeURLs)  + ' URLs that don´t appear to belong in thier site-collection'
		

	SET QUOTED_IDENTIFIER ON

	DECLARE @nonSiteCollectionHitsCursor as CURSOR;

	DECLARE @id as int;
	DECLARE @hit_url as nvarchar(max);
	DECLARE @site_url as nvarchar(max);

	SET @nonSiteCollectionHitsCursor = CURSOR FOR
		SELECT
			hits.id, urls.full_url, sites.url_base
		FROM hits 
			INNER JOIN urls on hits.url_id = urls.id
			inner join webs on webs.id = hits.web_id
			inner join sites on sites.id = webs.site_id
		where full_url not like (sites.url_base + '%')
				and full_url != sites.url_base
		open @nonSiteCollectionHitsCursor

		FETCH NEXT FROM @nonSiteCollectionHitsCursor INTO @id, @hit_url, @site_url
	
		WHILE @@FETCH_STATUS = 0
		BEGIN

			-- Delete hit
			delete from hits where id = @id
			print '"' + @hit_url + '" is not in site-collection "' + @site_url + '"'

			-- Get next hit with duplicates
			FETCH NEXT FROM @nonSiteCollectionHitsCursor INTO @id, @hit_url, @site_url

		END

	Close @nonSiteCollectionHitsCursor
	DEALLOCATE @nonSiteCollectionHitsCursor

-- Instructions: check output of script to make sure deletes simulated look ok.
-- Once sure, uncomment "commit" line, comment "rollback" line and run again to commit properly

rollback transaction nonSiteCollectionHitsTrans
--commit transaction nonSiteCollectionHitsTrans
