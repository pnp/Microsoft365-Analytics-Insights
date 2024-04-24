# SharePoint Online Components
These solutions are the two components used for SharePoint web session tracking. 
**AITracker** (Application Insights Tracker) – the main tracking logic. When built with node, gives a single JavaScript file that does all the tracking for modern & classic pages. On it’s own isn’t enough to be injected into modern pages, so uses…

**ModernPagesAITrackerExtension** – a bootstrapper for AITracker. Is installed for entire tenant usually, and just loads AITracker where it finds the custom-action registration. 

*Note*: AITracker is installed on an explicit site-collection whitelist basis. There is no tenant-wide deployment possible for both components. 
