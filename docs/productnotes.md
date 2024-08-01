# Design Documentation

Misc notes of how the system is expected to work is detailed here.

## Page Metadata Updates

As part of the client-side scripts to track page-views, the JavaScript components are also responsible for collecting SPO page metadata when a page is visited. 
Collection is done client-side to avoid having to crawl sites from the server which would require extra permissions to manage aside from the client-side deployment. However, the downside of having crawling done via client-side deployment is we need to throttle updates, as every visiting client is responsible for providing those updates.

Metadata collection is done with the AITracker JavaScript for pages that are new to each browser of every visitor. “New” is defined as “not visited with within X hours” (configurable, but 24 hours by default) and the values for "page(s) last visited" are stored within the “AITrackerPagesMetadataUploaded” local storage of the browser.

The process is two-step:
1. Client-side: Detect page navigation. If the page metdata has not be recently updated, load metadata: likes, comments, page properties. Send as an Application Insights "custom event" with name "PageMetadataUpdate". 
2. Server-side: given lots of browsers can send lots of PageMetadataUpdates, we only update pages in SQL Server that have a value of "file_last_refreshed" > X hours (configurable). 

### Client-Side Collection

![AITrackerPagesMetadataUploaded value](media/AITrackerPagesMetadataUploaded.png)

This client-side data tells the tracker for which pages in which lists we’ve read & sent page-properties to Application Insights for.

To avoid flooding the SharePoint Online API to load page info and Application Insights to send page info to the importer, the script implements these throttling controls.

*Note: page property values over 1000 characters in length are discarded, as some properties are very long in length.*

### Server-Side Updates
Finally, regardless on the updates sent to App Insights, on the server-side, pages are only updated that either haven’t been updated or were updated over a day ago (URL field “file_last_refreshed”).

![Page update process](media/page_updates_process.jpg)

This is all designed so the system can scale with lots of users/pages, without causing performance problems or too much cost for Application Insights.

All the above does mean however, that if you’re testing with just a couple of users to browse a site.

### Configuring Update Cadence
You can configure the cadence for updates by adding an environment variable ```MetadataRefreshMinutes``` to your importer configuration ([app service](https://learn.microsoft.com/en-us/azure/app-service/configure-common?tabs=portal#configure-app-settings) for example).

AITracker.js will attempt to read this configured value & more from the deployed app-service API on start-up, but default to 24 hours until it has successfully read this value.

_Important_: this import configuration is also cached locally with a local browser storage setting called ```AITrackerConfig```, for the same number of minutes that ```MetadataRefreshMinutes``` is set to.
