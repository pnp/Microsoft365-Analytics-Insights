import { postApiReturnJson } from "../Api";
import { debug, error } from "../Logger";
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
        const url = this.baseUrl + "/api/ImportConfig?appInsightsStringEncoded=" + this.appInsightsStringEncoded;
        console.debug("SPOI: Loading config from " + url);
        return postApiReturnJson<AITrackerConfig>(url)
            .then((config) => { return { config: config, success: true } })
            .catch((err) => {
                // If we can't load the config, we'll just use the default
                error("Failed to load config from " + url + ". Check JS console");
                error(err);
                return { config: AITrackerConfig.GetDefault(), success: false };
            });
    }
}
