import { postApiReturnJson } from "../Api";
import { error } from "../Logger";
import { AITrackerConfig } from "../Models";
import { ConfigLoadResult, IConfigLoader } from "./interfaces";

// Load config from API
export class ApiConfigLoader implements IConfigLoader {
    baseUrl: string;
    appInsightsStringEncoded: string;
    
    constructor(baseUrl: string, appInsightsStringEncoded: string) {
        this.baseUrl = baseUrl;
        this.appInsightsStringEncoded = appInsightsStringEncoded;
    }

    loadConfig(): Promise<ConfigLoadResult> {
        const baseUrl = this.baseUrl + "/api/ImportConfig";
        const url = baseUrl + "?appInsightsStringEncoded=" + this.appInsightsStringEncoded;
        console.debug("SPOI: Loading config from " + baseUrl);
        return postApiReturnJson<AITrackerConfig>(url)
            .then((config) => { return { config: config, success: true } })
            .catch((err) => {
                // If we can't load the config, we'll just use the default
                error("Failed to load config from " + baseUrl + ". Check App Service URL, status, and CORS settings.");
                error(err);
                return { config: AITrackerConfig.GetDefault(), success: false };
            });
    }
}
