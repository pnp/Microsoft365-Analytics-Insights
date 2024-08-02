![Design Header](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/media/design.jpg)

Welcome to the **Office 365 Advanced Analytics** project home.

This is an analytics engine that extracts much more analytics from M365 than is available out of the box. The core part of this solution is an ingestion engine that collects enhanced M365 usage data and stores it into a single SQL Server database. Other solutions then use this dataset to offer enhanced funcionality or reporting.

A list of data this engine can collect is below.

## Getting started

All the documentation is in the [wiki](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki).

* [Prerequisites](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Prerequisites)
* [Installation (with installer)](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Deployment%20Guidance)
* [Release notes](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Release%20Notes)
* [Known issues](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Known%20Issues)

There are several solutions built with the analytics engine:

* [Activity and Usage Analytics](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Analytics)
* Adoptify
* SharePoint Insights

Some of these solutions require additional configuration after the analytics engine has been installed. Please review the [post-install docs](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Deployment%20Guidance#additional-solutions).

## Collected data

>This is an excerpt of the whole details [found here](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki#data-collection-map).

This solution can store analytics data for many metrics for Office 365, if fully enabled.

**Note**: depending on your requirements, not all areas of the solution need to be fully enabled.

For all the data to be recorded for each area various permissions are needed, but if only a specific subset of statistics is required, the solution can work on a subset of permissions where appropriate.

### SharePoint Online (Web and Audit Log)

Usage focused on web-traffic & file usage activity, including:

* Web-browsing: page hits, clicks & user sessions
* File activity
* Searches
* Pages metadata
* Page comments and likes

### SharePoint Usage

* SharePoint user activity, daily.
* SharePoint site activity, weekly

### Teams & Calls

Statistics focused on adoption. All information is historical, and a snapshot kept each day the solution is running.

* Teams
* Channels in Teams
* Teams user activity
* Teams’ user device activity
* Calls & meetings

### Outlook Usage

High-level Outlook usage statistics.

* User activity

### OneDrive Usage

Statistics focused on adoption. All information is historical, and a snapshot kept each day the solution is running.

* User activity

### Yammer Messages & Usage

Statistics focused on groups & user activity. All information is historical, and a snapshot kept each day the solution is running.

* Yammer activity

### User Apps and Platforms Usage

Statistics that provides the details about which apps and platforms users have used, each day the solution is running.

* M365 apps usage

### Stream Activity

Statistics focused on stream activity. All information is historical, and a snapshot kept each day the solution is running.

* Stream activity

### Misc Audit Data

Given we already scan the audit logs for SharePoint, other workloads can be imported too, although by default this is disabled.

Element | Description
-|-
Exchange audit events | Operation (i.e., "New-Mailbox") User Timestamp
General audit events | Operation (i.e., "ViewReport"). User. Timestamp
Azure AD events | Operation (i.e., "UserLoggedIn"). User. Timestamp

### User Information & Attributes

For all users seen by the system & active in Azure AD, this information is read and stored:

* User details
* User SKUs

### Copilot Events

* Interactions with copilot (new chat thread)
* Files involved with copilot

## Azure Runtime Costs & Architecture

The solution uses various components in Azure to operate. Here’s what we use & expected performance tiers.

Element | Description | Expected Performance Tiers
-|-|-
Azure App Service (Windows) + service plan – **recommended**. | Used to host the importing web-jobs and the ASP.Net site for calls notifications (and administration site). | B1-S2. Production environments need at least 3.5GB RAM (B2 tiers) Pricing: <https://azure.microsoft.com/en-us/pricing/details/app-service/windows/>
Azure SQL Server – **recommended** | Endpoint for SQL database. | n/a – costs are done by database.
Azure SQL Database – **recommended** | Single source of reporting data. Usually this is the component that needs to be scaled when dataset requirements grow. | 10-50 DTUs without enhanced usage profiling enabled. 50-100 DTUs if enhanced usage profiling is optionally enabled (see below). Pricing: <https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/>
Azure Cache for Redis – **required**. | App caching for tokens and cognitive services lookups. | C0 Basic Pricing: <https://azure.microsoft.com/en-us/pricing/details/cache/>
Language Cognitive Service – **optional** | Used for language, keyword, and sentiment detection. | Standard Pricing: [https://azure.microsoft.com/en-us/pricing/details/cognitive-services/language-service/\#pricing](https://azure.microsoft.com/en-us/pricing/details/cognitive-services/language-service/#pricing)
Service Bus – **required** for Teams call-logging. | Used to queue call notifications asynchronously. | Basic Pricing: [https://azure.microsoft.com/en-us/pricing/details/service-bus/\#pricing](https://azure.microsoft.com/en-us/pricing/details/service-bus/#pricing)
Key vault – **required** for Adoptify only. | Used to store Graph app secrets for Adoptify. Read by logic apps. | Free - <https://azure.microsoft.com/en-us/products/key-vault>
Storage | Used for log & table storage if needed, and for storing any PowerShell SQL extensions needed in blob storage. | Pay-as-you-go. Normally zero costs unless detailed logging is turned on. <https://azure.microsoft.com/en-gb/pricing/details/storage/blobs/>
Automation Account | Used to run enhanced usage profiling if wanted/enabled separately. | Likely free – the first 500 minutes per month are free. Unless datasets are very large, it’s common to not need any more. <https://azure.microsoft.com/en-us/pricing/details/automation/>

Total estimated costs expected for a medium sized environment (20,000 users), for 1 year of data collection: **€170/month** approximately.

Expected data-range for a medium-sized environment:

* 10,000 hits & audit events daily.
* Up to 1000 teams.
* Up to 20,000 users.
* Up to 1 year of data-collection. Longer retention rates will require higher SQL performance tiers.

Some components can be moved out of Azure; the app-service and the SQL database if needed, but we recommend keeping it in Azure so the automatic installer update process will work with the architecture.

![Architecture diagram](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/media/architecture.jpg)
