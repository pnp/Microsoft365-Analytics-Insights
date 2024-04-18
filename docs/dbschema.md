# Database Schema Explanation

The result of the analytics imports is a single SQL Server database, which forms the basis for all the insights that can be extracted from the customers’ Office 365 usage.

The tables are created by Entity Framework when either web-job encounters a blank database.

These are what each table is used for & the data each one contains. No other storage facility is used to store analytics data.

**Important**: please only use the direct tables in the database schema, and not any of the views as some of the reports do.

## Common/Lookup Tables

These are tables that are shared across both imports/types of data.

| Table Name    | Comments                                                                                                              |
|---------------|-----------------------------------------------------------------------------------------------------------------------|
| urls          | Any URL seen in either an audit-log or SharePoint hit. URLs only.                                                     |
| sites         | Site-collections seen by the system. URL base only.                                                                   |
| users         | Users seen by the system. Contains an ID (email address), which can be anonymized if needed, plus the metadata below. |
| webs          | SharePoint web (or sub-web). URL & title. Links to a parent “site”.                                                   |
| yammer_groups | Yammer groups. Linked to by yammer_group_activity_log                                                                 |
| license_types | User licenses. Linked to by users.                                                                                    |
| languages     | Spoken languages in Teams channels.                                                                                   |
| keywords      | Keywords of chat in Teams channels.                                                                                   |

## User Metadata

All these tables relate to the users table mentioned above. All are populated from Azure AD metadata.

| Table Name                | Comments                                                        |
|---------------------------|-----------------------------------------------------------------|
| user_company_name         | Lookup table.                                                   |
| user_country_or_region    | Lookup table.                                                   |
| user_departments          | Lookup table.                                                   |
| user_job_titles           | Lookup table.                                                   |
| user_license_type_lookups | Lookup table linking user to a type of license (license_types). |
| user_office_locations     | Lookup table.                                                   |
| user_state_or_province    | Lookup table.                                                   |
| user_usage_locations      | Lookup table.                                                   |

User data is populated for all users on 1st execution, then only updated users from thereon (assuming the delta key is found in Redis).

## Web Sessions

These tables are specific for web-browsing activity only.

| Table Name    | Comments                                                                                                     |
|---------------|--------------------------------------------------------------------------------------------------------------|
| browsers      | Browser-name (includes version). “Edge”, “Chrome”, etc.                                                      |
| cities        | Cities lookup. Names only.                                                                                   |
| countries     | Every country any user has browsed from. Names only.                                                         |
| devices       | Devices lookup. PC, mobile, etc. Names only.                                                                 |
| hits          | Every hit tracked, linked to a parent session. It has various lookup foreign keys.                           |
| page_titles   | Page-title lookups.                                                                                          |
| provinces     | Provinces lookup. Names only.                                                                                |
| sessions      | A single session that will have 1+ hit. Contains a session ID                                                |
| page_likes    | Which users have liked which pages                                                                           |
| page_comments | Comments left on pages by whom. Comments have an optional parent ID if they are replies to another comment.  |

## SharePoint Files

| Table Name                       | Comments                                                                                                                                                                                                                                                          |
|----------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| file_metadata_property_values    | List properties for files seen in SharePoint. Populated by user web-traffic only to avoid needing application SharePoint site read permissions.   Also includes the taxonomy tag ID if the property is of that type.  Values longer than 1000 chars are ignored.  |
| file_field_definitions           | List of property names used in pages. Field names that are \> 100 chars in name length or that start “vti_x005f” (system fields) are ignored.                                                                                                                     |
| hits_clicked_elements            | Clicks made during a hit – URL, CSS class-names of the link, link title is included. The related hit is part of a session.                                                                                                                                        |
| hits_clicked_element_titles      | Clicked link titles lookup table.                                                                                                                                                                                                                                 |
| hits_clicked_element_class_names | Clicked link CSS names lookup table.                                                                                                                                                                                                                              |
| page_likes                       | Who liked which URL, and when. If users “unlike” something a “deleted” value is set.                                                                                                                                                                              |
| page_comments                    | Who commented on what page (URL) and when, with sentiment and language detection results if enabled in solution (otherwise null values for sentiment and language). If users delete a comment a “deleted” value is set.                                           |

As noted above, only links that don’t suppress the click event are tracked.

## Office 365 Activity Tables

There are two types of imports: daily and weekly aggregate.

The daily activity tables will have 1 new record per day, per active user (or Yammer group in the example of Yammer group activity). The system requests statistics per identity for each day (24 hour period) from “today”, back 7 days and updates the SQL tables for data received during that period.

Weekly aggregate tables refresh when the aggregate statistics are refreshed on the Sunday for the previous week.

Usually there’s up to a 2-3 day delay in activity to it being seen in the import.

| Table Name                      | Comments                                                                                                                                                                                                                                                                                                      |
|---------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| sharepoint_user_activity_log    | **Per user, per day:** User activity within SharePoint; online or via client applications (OneDrive etc). Per user: files shared, synced, and viewed/edited.  More information: <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/sharepoint-activity-ww?view=o365-worldwide>           |
| sharepoint_sites_file_stats_log | **Per site, per week**: Site usage statistics. Total vs active file counts, space used, etc. Statistics table is added to every Sunday for the previous week.  More information: <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/sharepoint-site-usage-ww?view=o365-worldwide>        |
| onedrive_user_activity_log      | **Per user, per day:** User activity within OneDrive; online or via client applications (OneDrive etc). Per user: files shared, synced, and viewed/edited.  More information: <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/onedrive-for-business-activity-ww?view=o365-worldwide>  |
| onedrive_usage_activity_log     | **Per user, per day:** User usage data for users. Active files storage used, etc - <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/onedrive-for-business-usage-ww?view=o365-worldwide>                                                                                                |
| outlook_user_activity_log       | **Per user, per day:** User activity within Outlook; online or via client applications (OneDrive etc). Per user: number of emails sent & received; meetings joined & attended.  <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/email-activity-ww?view=o365-worldwide>                |
| yammer_device_activity_log      | **Per user, per day:** Whether a user used: web, Windows Phone, Android, iPhone, iPad, or other devices to access Yammer.  <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/yammer-device-usage-report-ww?view=o365-worldwide>                                                         |
| yammer_group_activity_log       | **Per group**: posts, reads, likes, and members.   <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/yammer-groups-activity-report-ww?view=o365-worldwide>                                                                                                                              |
| yammer_user_activity_log        | **Per user, per day:** posts, reads, likes.  <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/yammer-activity-report-ww?view=o365-worldwide>                                                                                                                                           |
| platform_user_activity_log      | **Per user, per day:** which apps are used on which platforms (e.g. “excel_windows”). Use of app on any platform is also included (e.g. “excel”).  <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/microsoft365-apps-usage-ww?view=o365-worldwide>                                    |

## Audit Activity Tables

These tables are used exclusively for activity imports. The main tables are covered here – not all.

The population of these tables depends on what activity is being imported. The possible import workloads are:

-   SharePoint.
-   Azure Active Directory.
-   General.
-   Exchange.
-   DLP (not imported by default).

All the workload specific tables join back to the common table for common event-data.

| Table Name                 | Comments                                                                                                                 |
|----------------------------|--------------------------------------------------------------------------------------------------------------------------|
| audit_events               | Common events table to all workloads. Contains user, date-time, etc.                                                     |
| event_meta_sharepoint      | SharePoint specific event data. Foreign-keys to file-extensions, file-names, related SharePoint Web, item-type, and URL. |
| event_meta_exchange        | Exchange specific event data. Only extra data is “object ID”.                                                            |
| event_meta_general         | General auditing data. Only extra data is “workload”.                                                                    |
| event_meta_azure_ad        | Azure AD auditing events. No extra data.                                                                                 |
| event_meta_stream          | Stream events. Lookups to “client application”, and “video”.                                                             |
| ignored_audit_events       | Events that’ve been ignored (weren’t relevant to import)                                                                 |
| audit_event_azure_ad_props | Extra properties sent from Activity API for Azure AD events.                                                             |
| audit_event_exchange_props | Extra properties sent from Activity API for Exchange events.                                                             |

**Note**: the workloads that are imported are defined in “ContentTypesListAsString” configuration key of the activity importer.

## Teams

These tables are specific for Teams activity only.

| Table Name                       | Comments                                                                                                                               |
|----------------------------------|----------------------------------------------------------------------------------------------------------------------------------------|
| teams                            | Teams definitions – discovered date, last refresh date, Graph ID, and name                                                             |
| teams_addons                     | Teams add-on definitions – add-on type, published state, Graph ID, and name                                                            |
| teams_addons_log                 | Daily log of which add-ons are installed on which teams.                                                                               |
| teams_channel_stats_log          | Daily log of stats for all channels - \# chats, and sentiment score for the day.                                                       |
| teams_channel_stats_log_keywords | Lookup table linking a keyword to a channel-stats log                                                                                  |
| teams_channel_stats_log_langs    | Lookup table linking a language to a channel-stats log                                                                                 |
| teams_channel_tabs_log           | Daily log of tabs installed on which channels.                                                                                         |
| teams_channels                   | Team channels definitions – Graph ID, name, and parent Team                                                                            |
| teams_tabs                       | Tabs definitions – URL, add-on ID, Graph ID, and name                                                                                  |
| teams_user_activity_log          | Aggregate stats for Teams users per day – counts for private chats, meetings, and calls.                                               |
| teams_user_device_usage_log      | Aggregate stats for Teams user device usage per day – whether users used Teams on: web, Windows Phone, Android, iOS, Mac, and Windows. |
| team_membership_log              | Daily log of membership to a team – user & team for a given date.                                                                      |
| team_owners                      | Current owners of each Team – user, and Team lookups.                                                                                  |

## Calls

These tables are specific for Teams & Skype calls activity only.

| Table Name                   | Comments                                                                                                 |
|------------------------------|----------------------------------------------------------------------------------------------------------|
| call_records                 | List of calls made – organizer, call-type, Graph ID, start and end.                                      |
| call_failures                | List of call failures – parent call, reason given, stage of failure                                      |
| call_feedback                | Call feedback given by users – parent call, rating, extra comments, user in question.                    |
| call_modalities              | Call modalities lookup table (audio, video, screenshare, etc).                                           |
| call_session_call_modalities | List of modalities used in any given call session – parent call, and modality used.                      |
| call_sessions                | Sessions in a call. Anyone joining a call will have their own session in the call – attendee, start, end |
| call_types                   | Type of call lookup table only                                                                           |

## Stream

These tables are specific for Stream Videos activity only.

| Table Name    | Comments                                                                  |
|---------------|---------------------------------------------------------------------------|
| stream_videos | Definition of videos seen anywhere in analytics – name, and ID of stream  |

## Yammer

These tables are specific for Yammer activity only.

| Table Name                 | Comments                                                                                                |
|----------------------------|---------------------------------------------------------------------------------------------------------|
| yammer_messages            | Messages imported from Yammer – sender, created, Yammer ID, “reply to” ID, like count, followers count. |
| yammer_msg_to_stream       | Lookup table for linking Stream videos to Yammer messages for messages that have videos embedded.       |
| yammer_device_activity_log | With what devices are users accessing Yammer                                                            |
| yammer_group_activity_log  | Activity per group: messages posted, read, liked, number of members                                     |

## Miscellaneous Tables

These tables are for any other use.

| Table Name               | Comments                                                                      |
|--------------------------|-------------------------------------------------------------------------------|
| o365_client_applications | List of Office 365 client applications – name and Microsoft IDs for each one. |
| sys_telemetry_reports    | Telemetry report contents sent to Microsoft (disabled by default).            |
| sys_configs              | Configurations applied to system from control-panel.                          |
| \__MigrationHistory      | Entity Framework 6 migration log. Do not edit!                                |

