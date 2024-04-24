
begin transaction emptySearchesTrans

-- Delete Hits Outside of Site-Collection
	DECLARE @emptySearchTermSearchesCount as int;
	set @emptySearchTermSearchesCount = 
		(select count(*) from searches 
			inner join search_terms on searches.search_term_id = search_terms.id
			where [search_term] = '')

	print 'Found ' + convert(nvarchar(255), @emptySearchTermSearchesCount)  + ' search-terms that are empty'
	

	SET QUOTED_IDENTIFIER ON

	
	delete from searches where search_term_id in (select search_terms.id from search_terms where [search_term] = '')
	delete from search_terms where [search_term] = ''

-- Instructions: check output of script to make sure deletes simulated look ok.
-- Once sure, uncomment "commit" line, comment "rollback" line and run again to commit properly

rollback transaction emptySearchesTrans
--commit transaction emptySearchesTrans
