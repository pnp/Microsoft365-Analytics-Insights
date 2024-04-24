SELECT Search_Term, COUNT(*) as Search_Totals,
CASE WHEN Count(*) < 2 THEN '1 Search' WHEN Count(*) >= 2 AND count(*) < 10 THEN 'Under 10 Searches'
WHEN  COUNT(*) >= 10 AND COUNT(*) < 20 THEN '10 - 20 Searches'
WHEN COUNT(*) <= 20 AND COUNT(*) < 50 THEN  '20 - 50 Searches'
WHEN COUNT(*) <= 50 AND COUNT(*) < 100 THEN '50 - 100 Searches'
ELSE 'Over 100 Searches' END AS SearchBand 
FROM vwSearches
GROUP by search_term
