--This query creates a page load time dashboard
DROP TABLE IF EXISTS #T1
DROP TABLE IF EXISTS #T2
DROP TABLE IF EXISTS #T3
DROP TABLE IF EXISTS #T4
DROP TABLE IF EXISTS #T5
DROP TABLE IF EXISTS #T6
DROP TABLE IF EXISTS #T7

SELECT url_id, COUNT(url_id) AS Total INTO #T1 FROM dbo.hits
WHERE page_load_time < 1.00
GROUP BY url_id

SELECT url_id, COUNT(url_id) AS Total INTO #T2 FROM dbo.hits
WHERE page_load_time < 3.00 AND page_load_time >= 1.00
GROUP BY url_id

SELECT url_id, COUNT(url_id) AS Total INTO #T3 FROM dbo.hits
WHERE page_load_time < 5.00 AND page_load_time >= 3.00
GROUP BY url_id

SELECT url_id, COUNT(url_id) AS Total INTO #T4 FROM dbo.hits
WHERE page_load_time < 7.00 AND page_load_time >= 5.00
GROUP By url_id

SELECT url_id, COUNT(url_id) AS Total INTO #T5 FROM dbo.hits
WHERE page_load_time < 10.00 AND page_load_time >= 7.00
GROUP BY url_id

SELECT url_id, COUNT(url_id) AS Total INTO #T6 FROM dbo.hits
WHERE page_load_time < 15 AND page_load_time >= 10.00
GROUP BY url_id

SELECT url_id, COUNT(url_id) AS Total INTO #T7 FROM dbo.hits
WHERE page_load_time > 15
GROUP BY url_id



SELECT full_url, 
COALESCE(#T1.total,0) AS 'Under 1 Second'
, 
COALESCE(#T2.total,0) AS '1-3 Seconds'
,
COALESCE(#T3.total,0) AS '3-5 Seconds'
,
COALESCE(#T4.total,0) AS '5-7 Seconds'
,
COALESCE(#T5.total,0) AS '7-10 Seconds'
,
COALESCE(#T6.total,0) AS '10-15 Seconds'
,
COALESCE(#T7.total,0) AS 'Over 15 Seconds'
FROM dbo.urls as urls
LEFT JOIN #T1
ON urls.id = #T1.url_id

LEFT JOIN #T2
ON urls.id = #T2.url_id

LEFT JOIN #T3
ON urls.id = #T3.url_id

LEFT JOIN #T4
ON urls.id = #T4.url_id

LEFT JOIN #T5
ON urls.id = #T5.url_id
LEFT JOIN #T6
ON urls.id = #T6.url_id

LEFT JOIN #T7
ON urls.id = #T7.url_id

DROP TABLE IF EXISTS #T1
DROP TABLE IF EXISTS #T2
DROP TABLE IF EXISTS #T3
DROP TABLE IF EXISTS #T4
DROP TABLE IF EXISTS #T5
DROP TABLE IF EXISTS #T6
DROP TABLE IF EXISTS #T7
