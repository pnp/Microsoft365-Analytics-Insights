
CREATE OR ALTER VIEW [dbo].[vwTeamsCalls1] AS

    WITH CTE AS (
    SELECT call_session_id AS call_session_id,
    MAX(CASE WHEN call_session_call_modalities.call_modality_id = 1 THEN 'Yes' ELSE 'None' END) AS Audio,
    MAX(CASE WHEN call_session_call_modalities.call_modality_id = 2 THEN 'Yes'ELSE 'None' END) AS Video,
    MAX(CASE WHEN call_session_call_modalities.call_modality_id = 3 THEN 'Yes' ELSE 'None' END) As VideoScreenShare,
    MAX(CASE WHEN call_session_call_modalities.call_modality_id = 4 THEN 'Yes' ELSE 'None' END) AS ScreenShare
    FROM call_session_call_modalities
    GROUP By call_session_id
    )


    SELECT call_records.id AS Call_Records_id, call_types.name AS 'Call Type',  CTE.Call_session_id, case when users.[User_Name] is NULL then organizer.[user_name] else users.[user_name] end as 'Attendee Name', organizer.[User_Name] as 'Organizer Name', call_records.organizer_id AS 'Organizer', call_sessions.attendee_user_id AS 'Attendee', call_sessions.[start] AS 'Call Session Start', call_sessions.[end] AS 'Call Session End'
    , call_records.[start] AS 'Group Call start',
    call_records.[end] AS 'Group Call End',
    isnull(datediff(ss,call_records.[start],call_records.[end]),0) 'GroupcallDuration_Sec',
    case when datediff(s,convert(char(8),dateadd(s,datediff(s,call_records.[start],call_records.[end]),'1900-1-1'),8),convert(char(8),dateadd(s,datediff(s,call_sessions.[start],call_Sessions.[end]),'1900-1-1'),8)) is null then 100
    when datediff(s,convert(char(8),dateadd(s,datediff(s,call_records.[start],call_records.[end]),'1900-1-1'),8),convert(char(8),dateadd(s,datediff(s,call_sessions.[start],call_Sessions.[end]),'1900-1-1'),8)) = 0 then 100
    else
    isnull(cast(abs(datediff(s,convert(char(8),dateadd(s,datediff(s,call_records.[start],call_records.[end]),'1900-1-1'),8),convert(char(8),dateadd(s,datediff(s,call_sessions.[start],call_Sessions.[end]),'1900-1-1'),8))) as float)/isnull(datediff(ss,call_records.[start],call_records.[end]),1),100)*100 
    end as AttendePercentage,
    --isnull(cast(abs(datediff(s,convert(char(8),dateadd(s,datediff(s,call_records.[start],call_records.[end]),'1900-1-1'),8),convert(char(8),dateadd(s,datediff(s,call_sessions.[start],call_Sessions.[end]),'1900-1-1'),8))) as float)/isnull(datediff(ss,call_records.[start],call_records.[end]),1),100) as AttendePercentage,
    convert(char(8),dateadd(s,datediff(s,call_records.[start],call_records.[end]),'1900-1-1'),8) AS 'Group Call Duration',
    isnull(datediff(ss,call_sessions.[start],call_Sessions.[end]),0) as 'AttendeeCallDuration_Sec',
    convert(char(8),dateadd(s,datediff(s,call_sessions.[start],call_Sessions.[end]),'1900-1-1'),8) AS 'Attendee Session Duration',
    datediff(s,convert(char(8),dateadd(s,datediff(s,call_records.[start],call_records.[end]),'1900-1-1'),8),convert(char(8),dateadd(s,datediff(s,call_sessions.[start],call_Sessions.[end]),'1900-1-1'),8)) AS 'Session/Call Difference (Seconds)',
    call_feedback.rating, call_feedback.text,
    CTE.Audio,
    CTE.Video,
    CTE.VideoScreenShare,
    CTE.ScreenShare,
    dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
    dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear,
    dimtime.time, dimtime.hour, 
    dimtime.[Period of Day], DimTime.PeriodSort
    FROM call_records
    LEFT JOIN call_sessions
    ON call_sessions.call_record_id = call_records.id
    INNER JOIN call_types
    ON call_records.call_type_id = call_types.id
    LEFT JOIN CTE
    ON CTE.call_session_id = call_sessions.id
    LEFT JOIN users
    ON users.id = call_sessions.attendee_user_id
    INNER JOIN users as Organizer
    ON organizer.id = call_records.organizer_id
    INNER JOIN dimDate AS dimdate
    ON CONVERT(DATE, call_records.[start], 103) = dimdate.date
    INNER JOIN DimTime as dimtime
    ON FORMAT(call_records.[start], 'HH:mm') = dimtime.time
    LEFT JOIN call_feedback
    ON call_records.id = call_feedback.call_id
GO




CREATE OR ALTER VIEW [dbo].[vwTeamsGroupCallsSessions] AS

    WITH CTE AS (
    SELECT call_session_id AS call_session_id,
    MAX(CASE WHEN call_session_call_modalities.call_modality_id = 1 THEN 'Yes' ELSE 'None' END) AS Audio,
    MAX(CASE WHEN call_session_call_modalities.call_modality_id = 2 THEN 'Yes'ELSE 'None' END) AS Video,
    MAX(CASE WHEN call_session_call_modalities.call_modality_id = 3 THEN 'Yes' ELSE 'None' END) As VideoScreenShare,
    MAX(CASE WHEN call_session_call_modalities.call_modality_id = 4 THEN 'Yes' ELSE 'None' END) AS ScreenShare
    FROM call_session_call_modalities
    GROUP By call_session_id
    )

    SELECT call_records.id AS Call_Records_id, CTE.Call_session_id, users.[User_Name] as 'Attendee Name', organizer.[User_Name] as 'Organizer Name', call_records.organizer_id AS 'Organizer', call_sessions.attendee_user_id AS 'Attendee', call_sessions.[start] AS 'Call Session Start', call_sessions.[end] AS 'Call Session End'
    , call_records.[start] AS 'Group Call start',
    call_records.[end] AS 'Group Call End',
    convert(char(8),dateadd(s,datediff(s,call_records.[start],call_records.[end]),'1900-1-1'),8) AS 'Group Call Duration',
    convert(char(8),dateadd(s,datediff(s,call_sessions.[start],call_Sessions.[end]),'1900-1-1'),8) AS 'Attendee Session Duration',
    datediff(s,convert(char(8),dateadd(s,datediff(s,call_records.[start],call_records.[end]),'1900-1-1'),8),convert(char(8),dateadd(s,datediff(s,call_sessions.[start],call_Sessions.[end]),'1900-1-1'),8)) AS 'Session/Call Difference (Seconds)',
    call_feedback.rating, call_feedback.text,
    CTE.Audio,
    CTE.Video,
    CTE.VideoScreenShare,
    CTE.ScreenShare,
    dimdate.date, dimdate.WeekDayName AS dayName,  dimdate.MonthName, dimdate.Month AS monthNumber, 
    dimDate.WeekOfYear, dimdate.quarter, dimdate.Year, dimdate.monthYear,
    dimtime.time, dimtime.hour, 
    dimtime.[Period of Day], DimTime.PeriodSort
    FROM call_records
    LEFT JOIN call_sessions
    ON call_sessions.call_record_id = call_records.id
    LEFT JOIN CTE
    ON CTE.call_session_id = call_sessions.id
    LEFT JOIN users
    ON users.id = call_sessions.attendee_user_id
    INNER JOIN users as Organizer
    ON organizer.id = call_records.organizer_id
    INNER JOIN dimDate AS dimdate
    ON CONVERT(DATE, call_records.[start], 103) = dimdate.date
    INNER JOIN DimTime as dimtime
    ON FORMAT(call_records.[start], 'HH:mm') = dimtime.time
    LEFT JOIN call_feedback
    ON call_records.id = call_feedback.call_id
    WHERE call_records.call_type_id = 1
GO


