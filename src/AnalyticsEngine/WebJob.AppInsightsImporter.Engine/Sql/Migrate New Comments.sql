/****** Script for SelectTopNRows command from SSMS  ******/

-- Insert without parent links yet
insert into page_comments(comment, language_id, user_id, sp_id, created, url_id, sentiment_score)
SELECT [comment]
      ,[language_id]
      ,[user_id]
      ,[sp_id]
      ,[created]
      ,[url_id]
      ,[sentiment_score]
  FROM [debug_staging_comments]

-- Find comments without a parent set, but that the temp table has a parent for
DECLARE comments_to_update_cursor CURSOR  
    FOR 
		select originalComments.id, debug_staging_comments.ParentCommentSharePointId, originalComments.url_id from page_comments originalComments
		inner join debug_staging_comments on originalComments.sp_id = debug_staging_comments.[sp_id]
		where 
			ISNULL(originalComments.parent_id, 0) = 0
			and ISNULL(debug_staging_comments.ParentCommentSharePointId, 0) > 0

-- Open the cursor
OPEN comments_to_update_cursor
DECLARE @comment_id_to_update INT
DECLARE @comment_url_id_to_update INT
DECLARE @comment_parent_spid INT

-- Fetch each page_comment that needs an updated parent ID
FETCH NEXT FROM comments_to_update_cursor INTO @comment_id_to_update, @comment_parent_spid, @comment_url_id_to_update
WHILE @@FETCH_STATUS = 0
BEGIN

	-- Find the existing page_comment that matches the sp_id of this parent sp_id
	update page_comments set parent_id = 
	(
		select top 1 id from page_comments where sp_id = @comment_parent_spid and page_comments.url_id = @comment_url_id_to_update
	)
	where page_comments.id = @comment_id_to_update 

    FETCH NEXT FROM comments_to_update_cursor INTO @comment_id_to_update, @comment_parent_spid, @comment_url_id_to_update
END

-- Close and deallocate the cursor
CLOSE comments_to_update_cursor
DEALLOCATE comments_to_update_cursor
