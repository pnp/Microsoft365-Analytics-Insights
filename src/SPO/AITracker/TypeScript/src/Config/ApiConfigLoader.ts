import { postApiReturnJson } from "../Api";
import { debug } from "../Logger";
import { AITrackerConfig } from "../Models";
import { IConfigLoader } from "./interfaces";

// Load config from API
export class ApiConfigLoader implements IConfigLoader {
    baseUrl: string;
    appInsightsStringEncoded: string;
    constructor(baseUrl: string, appInsightsStringEncoded: string) {
        this.baseUrl = baseUrl;
        this.appInsightsStringEncoded = appInsightsStringEncoded;
    }

    loadConfig(): Promise<AITrackerConfig> {
        const url = this.baseUrl + "/api/ImportConfig?appInsightsStringEncoded=" + this.appInsightsStringEncoded;
        debug("Loading config from " + url);
        return postApiReturnJson<AITrackerConfig>(url);
    }
}
