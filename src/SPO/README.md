# SharePoint Online Components
These solutions are the two components used for SharePoint web session tracking. 
**AITracker** (Application Insights Tracker) – all the tracking logic. Built with node, it gives a single JavaScript file, `AITracker.js`, that does all the tracking for modern & classic pages. On classic pages it runs on its own, loaded by a `ScriptLink` user custom action. On modern pages it needs a bootstrapper, so it uses…

**ModernPagesAITrackerExtension** – an SPFx application customizer that bootstraps AITracker on modern pages. It loads the site collection's own copy of `AITracker.js` and tracks nothing itself.

*Note*: the extension's `.sppkg` is deployed tenant-wide (`skipFeatureDeployment: true`), but that only makes the extension available. Tracking is enabled per site collection: the installer adds the site custom actions that carry the extension's properties, and uploads a copy of `AITracker.js` to each tracked site collection's `SPOInsights` library. Only the site collections the installer has set up are tracked. See [`.github/copilot-instructions.md`](.github/copilot-instructions.md) for how the two parts work together.
