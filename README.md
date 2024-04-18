*-Source coming soon-*

This document describes the steps required to deploy and verify the “Office 365 Advanced Analytics Engine” for any solutions based on it.

Each delivery will have solution specific steps you need to address too – **review the solution specific deployment document before continuing**.

There are several prerequisites needed though so please take time to check if these are in place before starting this deployment for the first time.

**Important**: this guide explains the full setup for all areas of the analytics engine. You may not need all parts of the solution, so please be clear which areas of data-collection are needed first, so the right permissions & prerequisites are clear too.

A complete list of data this engine can collect is below. Troubleshooting guides for common errors and problems are also in this document.

### System Docs
[Prerequisites](docs/prerequisites.md)

[Installation](docs/install.md)

[Verify deployment](docs/verify.md)

[Monitoring system](docs/monitoring.md)

[Troubleshooting](docs/troubleshooting.md)

[Known issues](docs/knownissues.md)

# Data Collection Map

This solution can store analytics data for the following metrics for Office 365, if fully enabled.

**Note**: depending on your requirements, not all areas of the solution need to be fully enabled.

For all the data to be recorded for each area various permissions are needed, but if only a specific subset of statistics is required, the solution can work on a subset of permissions where appropriate.

## SharePoint Online (Web and Audit Log)

Usage focused on web-traffic & file usage activity.

| Element                                         | Description                                                                                                                                                                                                                                                                                                                                        |
|-------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Web-browsing: page hits, clicks & user sessions | Browser sessions (start & end browsing). Hits in each session with time on page. Performance data – server-side & client-side rendering times. User location information: country, province, city. Browser & device used. Hit associated site-collection & web. Links clicked on in each page of a session\* Link text, CSS class names, and URL.  |
| File activity                                   | File events: view, edit, delete etc, for any files in the configured sites.                                                                                                                                                                                                                                                                        |
| Searches                                        | Searches made from the SharePoint Online website.                                                                                                                                                                                                                                                                                                  |
| Pages metadata                                  | All page properties & metadata associated with visited pages; list properties & taxonomies.                                                                                                                                                                                                                                                        |
| Page comments and likes                         | For SharePoint pages with comments and likes (for the page), they are read by the JavaScript tracker and stored in the SQL database.  Comments are stored too with a sentiment score & detected language if a text analytics service is configured.                                                                                                |

\*Link clicks tracked where possible. Some links suppress click events so cannot be seen by our tracking code.

## SharePoint Usage

| Element                                                                                                                                                                                                                                                                                                                                                                                                                       | Description                                                                                                                                                                                                                                            |
|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| SharePoint user activity, daily. More information on fields in activity reports: [https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide\#which-activity-reports-are-available-in-the-admin-center](https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide#which-activity-reports-are-available-in-the-admin-center)  | By user: Files viewed or edited. Files synced. Files shared internally. Files shared externally.                                                                                                                                                       |
| SharePoint site activity, weekly                                                                                                                                                                                                                                                                                                                                                                                              | By site per week: External Sharing count File Count Active File Count Page View Count Visited Page Count Anonymous Link Count Company Link Count Secure Link for Guest Count Secure Link for Member Count Storage Used (Byte) Storage Allocated (Byte) |

## Teams & Calls

Statistics focused on adoption. All information is historical, and a snapshot kept each day the solution is running.

| Element                                                                                                                                                                              | Description                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Teams                                                                                                                                                                                | Owners & membership. Channels (see below). Add-ins & tabs deployed.                                                                                                                                                                                                                                                                                                                                                                                              |
| Channels in Teams                                                                                                                                                                    | Tabs deployed. Chat count. Sentiment score of chats. Chat keywords. Chat languages.                                                                                                                                                                                                                                                                                                                                                                              |
| Teams user activity.  More information on fields: <https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/microsoft-teams-user-activity-preview?view=o365-worldwide>  | By user: Private chat count. Team chat count. Calls count. Meetings count. Ad-hoc meetings attended count. Ad-hoc meetings organized count. Meetings attended count. Meetings organized count. Scheduled one-time meetings attended count. Scheduled one-time meetings organized count. Scheduled recurring meetings attended count. Scheduled recurring meetings organized count. Audio duration seconds. Video duration seconds. Screenshare duration seconds. |
| Teams’ user device activity                                                                                                                                                          | If each user has used: Web interface. iOS. Android. Windows Phone. Mac. Windows.                                                                                                                                                                                                                                                                                                                                                                                 |
| Calls & meetings                                                                                                                                                                     | Call start & end. Type (group/peer2peer). Call organizer. “Sessions” (each connected person is a separate session) Start & end. Attendee. Modalities (audio/video/screenshare). Quality feedback given (if any): Star-rating. Comments. Failures detected (if any): Reason. Stage.                                                                                                                                                                               |

## Outlook Usage

High-level Outlook usage statistics.

| Element                                                                                                                                                                                                                                                                                                                                                                                                      | Description                                                                                                        |
|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------|
| User activity.  More information on fields in activity reports: [https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide\#which-activity-reports-are-available-in-the-admin-center](https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide#which-activity-reports-are-available-in-the-admin-center)  | By user: Email send count. Email receive count. Email read count. Meeting created count. Meeting interacted count. |

## OneDrive Usage

Statistics focused on adoption. All information is historical, and a snapshot kept each day the solution is running.

| Element                                                                                                                                                                                                                                                                                                                                                                                                      | Description                                                                                                                                                  |
|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|
| User activity.  More information on fields in activity reports: [https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide\#which-activity-reports-are-available-in-the-admin-center](https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide#which-activity-reports-are-available-in-the-admin-center)  | By user: Files viewed or edited. Files synced. Files shared internally. Files shared externally. Storage used in bytes. Active file count. Total file count. |

## Yammer Messages & Usage

Statistics focused on groups & user activity. All information is historical, and a snapshot kept each day the solution is running.

| Element                                                                                                                                                                                                                                                                                                                                                                                                        | Description                                                                                                                                                                                                    |
|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Yammer activity.  More information on fields in activity reports: [https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide\#which-activity-reports-are-available-in-the-admin-center](https://docs.microsoft.com/en-us/microsoft-365/admin/activity-reports/activity-reports?view=o365-worldwide#which-activity-reports-are-available-in-the-admin-center)  | By user: Posted count. Read count. Liked count. By group: Posted count. Read count. Liked count. Member count By all messages (global - in preview): Total count. Likes count. Replies count. Followers count. |

## User Apps and Platforms Usage

Statistics that provides the details about which apps and platforms users have used, each day the solution is running.

| Element                                                                                                                                  | Description                                                                                                    |
|------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------|
| M365 apps usage <https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/microsoft365-apps-usage-ww?view=o365-worldwide>  | By user: Apps (Outlook, Word, Excel, PowerPoint, OneNote, and Teams) Platforms (Windows, Mac, Web, and Mobile) |

## Stream Activity

Statistics focused on stream activity. All information is historical, and a snapshot kept each day the solution is running.

| Element                                                                                                                                                                                           | Description                                                                                                            |
|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------|
| Stream activity.  More information on Stream events - <https://techcommunity.microsoft.com/t5/microsoft-stream-archive/microsoft-stream-audit-events-now-in-office-365-security-amp/ba-p/285203>  | By user: Videos watched & edited, with which client application. Streams-to-Yammer post relation for embedded streams. |

## Misc Audit Data

Given we already scan the audit logs for SharePoint, other workloads can be imported too, although by default this is disabled.

| Element                | Description                                        |
|------------------------|----------------------------------------------------|
| Exchange audit events  | Operation (i.e., “New-Mailbox”) User  Timestamp    |
| General audit events   | Operation (i.e., “ViewReport”). User. Timestamp.   |
| Azure AD events        | Operation (i.e., “UserLoggedIn”). User. Timestamp. |

## User Information & Attributes

For all users seen by the system & active in Azure AD, this information is read and stored:

| Element   | Description                                                                                                     |
|-----------|-----------------------------------------------------------------------------------------------------------------|
| Users     | Email. UPN. Office location. Usage location. Company name. Department. Job title. Manager. Azure AD identifier. |
| User SKUs | Assigned SKU for each user.                                                                                     |

The SQL database table schema is explained below.

## Copilot Events

| Element                                                                                                                          | Description                                                                                                       |
|----------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------|
| Interactions with copilot (new chat thread) <https://learn.microsoft.com/en-us/office/office-365-management-api/copilot-schema>  | Date User App host (the type of Copilot used during the interaction – either Teams/Bing, or within an Office app) |
| Files involved with copilot                                                                                                      | File name                                                                                                         |

# Azure Runtime Costs & Architecture

The solution uses various components in Azure to operate. Here’s what we use & expected performance tiers.

| Element                                                       | Description                                                                                                            | Expected Performance Tiers                                                                                                                                                                                               |
|---------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Azure App Service (Windows) + service plan – **recommended**. | Used to host the importing web-jobs and the ASP.Net site for calls notifications (and administration site).            | B1-S2. Production environments need at least 3.5GB RAM (B2 tiers) Pricing: <https://azure.microsoft.com/en-us/pricing/details/app-service/windows/>                                                                      |
| Azure SQL Server – **recommended**                            | Endpoint for SQL database.                                                                                             | n/a – costs are done by database.                                                                                                                                                                                        |
| Azure SQL Database – **recommended**                          | Single source of reporting data. Usually this is the component that needs to be scaled when dataset requirements grow. | 10-50 DTUs without enhanced usage profiling enabled. 50-100 DTUs if enhanced usage profiling is optionally enabled (see below). Pricing: <https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/>  |
| Azure Cache for Redis – **required**.                         | App caching for tokens and cognitive services lookups.                                                                 | C0 Basic Pricing: <https://azure.microsoft.com/en-us/pricing/details/cache/>                                                                                                                                             |
| Language Cognitive Service – **optional**                     | Used for language, keyword, and sentiment detection.                                                                   | Standard Pricing: [https://azure.microsoft.com/en-us/pricing/details/cognitive-services/language-service/\#pricing](https://azure.microsoft.com/en-us/pricing/details/cognitive-services/language-service/#pricing)      |
| Service Bus – **required** for Teams call-logging.            | Used to queue call notifications asynchronously.                                                                       | Basic Pricing: [https://azure.microsoft.com/en-us/pricing/details/service-bus/\#pricing](https://azure.microsoft.com/en-us/pricing/details/service-bus/#pricing)                                                         |
| Key vault – **required** for Adoptify only.                   | Used to store Graph app secrets for Adoptify. Read by logic apps.                                                      | Free - <https://azure.microsoft.com/en-us/products/key-vault>                                                                                                                                                            |
| Storage                                                       | Used for log & table storage if needed, and for storing any PowerShell SQL extensions needed in blob storage.          | Pay-as-you-go. Normally zero costs unless detailed logging is turned on. <https://azure.microsoft.com/en-gb/pricing/details/storage/blobs/>                                                                              |
| Automation Account                                            | Used to run enhanced usage profiling if wanted/enabled separately.                                                     | Likely free – the first 500 minutes per month are free. Unless datasets are very large, it’s common to not need any more.  <https://azure.microsoft.com/en-us/pricing/details/automation/>                               |

Total estimated costs expected for a medium sized environment (20,000 users), for 1 year of data collection: **€170/month** approximately…

Expected data-range for a medium-sized environment:

-   10,000 hits & audit events daily.
-   Up to 1000 teams.
-   Up to 20,000 users.
-   Up to 1 year of data-collection. Longer retention rates will require higher SQL performance tiers.

Some components can be moved out of Azure; the app-service and the SQL database if needed, but we recommend keeping it in Azure so the automatic installer update process will work with the architecture.
