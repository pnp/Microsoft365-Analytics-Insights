# Data model description

## Analytics SQL tables

The following tables are used by the report:

Table | Description | Data type
-|-|-
ActivitiesWeekly | Pivoted Activity metrics. Data in rows. | `bigint`
ActivitiesWeeklyColumns | Activity metrics | `bigint`
UsageWeekly | Platform metrics | `bool`

Additional tables:

Table | Description
-|-
TraceLogs | Aggregation logs

## Available metrics and data

Even though the data available in the APIs is aggregated daily, the report shows the data aggregated weekly.
We sum the activity from Monday to Sunday.
The date shown in the report will be a Monday, that represents the activity of the week that starts that Monday.

The following metrics show the number of activities of this type performed by the user. These are available in the `ActivitiesWeekly` and `ActivitiesWeeklyColumns` tables.

### Activity metrics

**OneDrive**
* Shared Externally
* Shared Internally
* Synced
* Viewed/Edited

**Outlook**
* Emails Read 
* Emails Received
* Emails Sent 
* Outlook Meetings Created
* Outlook Meetings Interacted

**SharePoint**
* Shared Externally
* Shared Internally
* Synced
* Viewed/Edited

**Teams**
* Private Chats
* Calls
* Meetings Attended
* Meetings Organized
* Adhoc Meetings Attended
* Adhoc Meetings Organized
* Scheduled OneTime Meetings Attended
* Scheduled OneTime Meetings Organized
* Scheduled Recurring Meetings Attended
* Scheduled Recurring Meetings Organized
* Audio Duration Seconds
* Video Duration Seconds
* Screenshare Duration Seconds
* Urgent Messages
* Post Messages
* Reply Messages

**Yammer**
* Liked
* Posted
* Read

**Copilot**

Hosts where Copilot has been used:
* Assist365
* Bing
* BashTool
* DevUI
* Excel
* Loop
* M365AdminCenter
* M365App
* Office
* OneNote
* Outlook
* Planner
* PowerPoint
* SharePoint
* Stream
* Teams
* VivaCopilot
* VivaEngage
* VivaGoals
* Whiteboard
* Word

**Copilot interactions**

* Number of chat interactions.
* Number of files where there was an interaction (ie. used Copilot to create a slide in PowerPoint).
* Number of meeting where Copilot was used.

### Usage metrics

The following metrics' values are true/false. These are available in the `UsageWeekly` table.

**M365 Apps (Office apps)**

| Totals per OS | Totals per App |
|-|-
| <ul><li>Windows</li><li>Mac</li><li>Mobile</li><li>Web</li></ul>|<ul><li>Outlook</li><li>Word</li><li>Excel</li><li>Powerpoint</li><li>Onenote</li><li>Teams</li></ul> | <ul><li>Outlook Windows</li><li>Word Windows</li><li>Excel Windows</li><li>Powerpoint Windows</li><li>Onenote Windows</li><li>Teams Windows</li></ul> | <ul><li>Outlook Mac</li><li>Word Mac</li><li>Excel Mac</li><li>Powerpoint Mac</li><li>Onenote Mac</li><li>Teams Mac</li></ul>

App per platform

| Windows | Mac | Mobile | Web
|-|-|-|-
| <ul><li>Outlook Windows</li><li>Word Windows</li><li>Excel Windows</li><li>Powerpoint Windows</li><li>Onenote Windows</li><li>Teams Windows</li></ul> | <ul><li>Outlook Mac</li><li>Word Mac</li><li>Excel Mac</li><li>Powerpoint Mac</li><li>Onenote Mac</li><li>Teams Mac</li></ul> | <ul><li>Outlook Mobile</li><li>Word Mobile</li><li>Excel Mobile</li><li>Powerpoint Mobile</li><li>Onenote Mobile</li><li>Teams Mobile</li></ul> | <ul><li>Outlook Web</li><li>Word Web</li><li>Excel Web</li><li>Powerpoint Web</li><li>Onenote Web</li><li>Teams Web</li></ul>

**Teams Devices by Platform/OS**
* Used Web
* Used Mobile
* Used Windows
* Used Linux
* Used Mac
* Used Chrome
* Used iOS
* Used Android
* Used WinPhone

**Yammer Devices by Platoform/OS**
* Used Web
* Used Mobile
* Used Others
* Used iPhone
* Used iPad
* Used Android
* Used WinPhone
* Platform Count

## Raw data

The raw data for the report can be found in the following tables of the database:

### Activity tables

* dbo.onedrive_user_activity_log
* dbo.outlook_user_activity_log
* dbo.platform_user_activity_log
* dbo.sharepoint_user_activity_log
* dbo.teams_user_activity_log
* dbo.teams_user_device_usage_log
* dbo.yammer_device_activity_log
* dbo.yammer_user_activity_log

### User details

* dbo.users
* dbo.user_license_type_lookups
* dbo.user_company_name
* dbo.user_country_or_region
* dbo.user_departments
* dbo.user_job_titles
* dbo.user_license_type_lookups
* dbo.user_office_locations
* dbo.user_state_or_province
* dbo.user_usage_locations
* dbo.provinces
* dbo.license_types
* dbo.countries
* dbo.cities

### Audit events

* dbo.audit_events
* dbo.event_copilot_chats
* dbo.event_copilot_files
* dbo.event_copilot_meetings
