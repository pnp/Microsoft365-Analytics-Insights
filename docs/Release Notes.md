## master - build 1400
Reliability updates for copilot event processing. Previously, SharePoint metadata lookups failed for file specific actions. Also now misc chats are better imported for Copilot actions in Outlook and other app-hosts. 

## master - build 1375
Fixes for SharePoint site aggregation usage stats. When looking up a site ID for a previously imported site (with no ID set), the site lookup save would crash. 

No SQL schema changes.

## master - build 1365
Installer supports creating Azure resources with tags for everything in the solution. 

Fixes around "enhanced usage profiling" - installer generates SAS URLs with a validity of 2hrs read-only for each file to be created as a runbook and sets that as the content-link for the runbook. Previously it created a container with anonymous read-only for blobs + shared the blob URLs without any parameters, as that seemed to cause problems with the Automation API. 
Note: runbook schedules still need manually enabling in case the aggregation stats aren't needed, as it generates a lot of extra load on the DB.

No SQL schema changes.

## master - build 1347
Installer: now supports deploying the "enhanced usage profiling" runbook resources + SQL extensions when updating DB schema. 
Will deploy runbook ARM template when usage imports are enabled, but runbook schedules need configuring manually (see engine install doc)

Activity import: will only import once every 24 hours, as that's the earliest timeframe for any updates. 

No SQL schema changes.

## master - build 1307
* Fixes:
  * "storage_used_bytes" in "sharepoint_sites_file_stats_log" resized from int to bigint as import was crashing for large sites.
  * New field "site_id" on "sites" - the Graph ID of each site. Nullable, and filled in when resolving site ID to URL from SharePoint Site Usage report loading.

SQL schema updated to 202404031636545_ExtendedUsageReportsSpSiteUsageLogDbFixes

## master - 1301
* Fixes:
  * platform_user_activity_log not populating activity correctly. 

No SQL schema changes. 

## master - 1286
* Fixes:
  * Crash on saving to teams_addons when multiple add-in names shared same ID.

* "users" table now has "mail" column for primary email address
* New usage imports:
  * User app usage (platform_user_activity_log) - which OS users use, and which Office apps. Daily stats.
  * SharePoint site usage stats (sharepoint_sites_file_stats_log) - how many active vs total files, links shared. Weekly stats that are added to when the dataset refresh is a Sunday (usually a couple of days after Sunday as Graph usage API is a couple of days behind).
* Expanded imports:
  * teams_user_device_usage_log now has "used_linux" and "used_chrome_os"
  * teams_user_activity_log now has "post_messages", "reply_messages", and "urgent_messages" counts.

SQL schema updated to 202403131104274_ExtendedUsageReports

## master - 1271
* Reliability fixes for SPO comments and metadata imports.

No SQL schema changes. 

## master - 1262
* NuGet updates to remove deprecated & vulnerable libraries, Azure Identity especially. 
* Authenticate with Key Vault cert instead of secret.
* New: page comments and page likes + DB schema changes.
  * New tables: page_likes, page_comments.
  * AI Tracker: version 1.5.2 sends these as separate lists, instead of just accumulated totals.
  * App Insights Importer will process new comments/likes data if seen in App Insights. 
  * See "3.1	Page Metadata Updates" in "O365 Adv Analytics and Insights - Deployment Guide" for information on how this works. 

Fixes:
* Search terms imports Greek chars correctly.
* App Insights DeviceName is imported correctly.
* URL importing ignores query parameters and bookmarks.
  * Now "https://contoso.sharepoint.com/sites/ProjectFalcon-UXtest?refresh=1" and "https://contoso.sharepoint.com/sites/ProjectFalcon-UXtest" are treated as the same page. 
* Large cognitive API calls are batched correctly and extra validation added to requests. 

Installer:
* Installer grants web-app managed ID to key-vault
* App service created with managed identity

SQL schema update required! 202401191004335_PageCommentsAndLikes


## master - 1236
Advanced analytics:
* AITracker for SPO sites now uses Application Insights connection-string instead of just instrumentation key.
  * Important: this version of the SPO components can *only* be deployed with this version (or above) of the installer/PowerShell scripts. Using a previous version of either will break tracking. 
  * SPOInsights ModernUI: 1.0.1.51
  * AI Tracker: version 1.5.1
* Installer 
  * Reliability fixes. 
  * Configured correctly the Language Service in the App Service
  * FTP validation test now will only work with own-configured FTPS target, if configured.
* Search events are imported from App Insights when user searched in SPO.
* Recommended: after updating, clear browser cache to ensure both latest scripts are loaded.

Adoptify:
* UI updates. Adoptify package 1.0.0.8

No SQL schema changes. 


## master - build 1226
Advanced analytics:
* AITracker improved click detection, now with duplicate-click detection too. New version: "1.4.9".
* Installer reliability fixes for FTP profile detection and UI tweaks for cognitive options. 

Adoptify:
* Adoptify 1.0.0.7 package + installer schema changes.
  * Added 'Custom Quests' functionality - quests that can be approved by admin as opposed to auto tracked
  * Added columns to lists to support custom quests

No SQL schema changes. 


## master - build 1207
Advanced analytics:
* Web reliability updates for page clicks.
* Event timestamp uses custom property value created by AITracker JS instead of app-insights event timestamp, for better accuracy. App Insights event wouldn’t necessarily arrive in order and on time the event was generated – the queuing and sending is done “when possible”. 
* Import job updates for increased resiliency.

Adoptify:
* Updated solutions to 1.0.3 – various reliability changes.

No SQL schema changes. 


## master - build 1196
* Page likes & comments read from API. Comments aren't in the database yet in detail, but "CommentsCount" and "LikesCount" are added as part of page properties.
* Page properties are only read once per day, per page.
* Clicks registers on anchor elements that use stopPropagation/event suppression.
    - For element clicks are aren't an A link (like a span), the parent A link is assumed, if there is one.
* Installer reliability fixes for SPFx installation & content uploading.

* If you allowed us: anonymous runtime stats are now sent to https://m365advancedanalytics.azurewebsites.net/api/Telemetry
    - Reports sent are saved in your SQL database, in sys_telemetry_reports. Example report:

        {
            "AnonClientId": "62FEBDCE7A104437AFAEB68F8954FBF818243C8649F7A8775B39FCDA2F1E4B6C",
            "id": "62FEBDCE7A104437AFAEB68F8954FBF818243C8649F7A8775B39FCDA2F1E4B6C",
            "DataPointsFromAITotal": 10,
            "ConfiguredSolutionsEnabledDescription": "CustomOrInsights",
            "ConfiguredImportsEnabledDescription": "Calls=True;GraphUsersMetadata=True;GraphUserApps=True;GraphUsageReports=True;GraphTeams=True;ActivityLog=True;WebTraffic=True",
            "TableStats": [
                {
                    "TableName": "users",
                    "TotalSpaceMB": 0.70,
                    "Rows": 1
                }
                ...
            ],
            "BuildVersionLabel": "Build 1000",
            "Generated": "2023-03-31T09:09:57.5461964+02:00"
        }

* New SPOInsights AI Tracker version:   v1.4.5
* New SPOInsights ModernUI version:     v1.0.14
* Adoptify solution v1.0.0.1



## master build 1169: 

Fixes:
* Fixed an issue with null caller ID crashing new call saving.
* Installer “save as” option was always disabled. 
* Adoptify solution zip updated.

No database schema changes from build 1164.
