
select 
	search_term_id
	,search_terms.search_term
	,term_count
from 
(
	SELECT top 10
		search_term_id
		, COUNT(*) as term_count
	FROM dbo.searches
	Group By search_term_id
	Order By term_count DESC
) 
	termStats
inner join search_terms on search_term_id = search_terms.id

