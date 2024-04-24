
-- ============================================
-- Author: Glenn Pepper
-- Create Date: 03/08/2020
-- Version: 2.0
-- Description: This view displays search information
-- This was amended to include a view for Searches for past 6 months
-- =============================================


IF OBJECT_ID('vwSearches','v') IS NOT NULL
BEGIN
DROP VIEW vwSearches
END
GO

CREATE VIEW vwSearches
AS
SELECT searches.id, searches.session_id, search_terms.search_term, users.id as 'User_ID'
FROM searches
LEFT JOIN search_terms
ON searches.id = search_terms.id
LEFT JOIN sessions 
ON searches.session_id = sessions.id
LEFT JOIN users 
ON sessions.user_id =users.id 
GO


IF OBJECT_ID('vwSearches_6Months','v') IS NOT NULL
BEGIN
DROP VIEW vwSearches_6Months
END
GO

CREATE VIEW vwSearches_6Months
AS
SELECT searches.id, searches.session_id, search_terms.search_term, users.id as 'User_ID'
FROM searches
LEFT JOIN search_terms
ON searches.id = search_terms.id
LEFT JOIN sessions 
ON searches.session_id = sessions.id
LEFT JOIN users 
ON sessions.user_id =users.id
LEFT JOIN hits 
on searches.id = hits.id
WHERE hits.hit_timestamp <= getdate() 
AND hits.hit_timestamp >= (DateAdd(MM, -6, DATEADD(month, DATEDIFF(month, 0, getdate()), 0)))
GO