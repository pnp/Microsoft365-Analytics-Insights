import { LocalStorageUtils } from "../LocalStorageUtils";
import { debug, log } from "../Logger";
import { AITrackerConfig } from "../Models";
import { ConfigLoadResult, IConfigLoader } from "./interfaces";

const cacheName = 'AITrackerConfig';
export class ConfigHandler {

    loader: IConfigLoader;
    _localStorageWorking: boolean;
    constructor(loader: IConfigLoader) {
        this._localStorageWorking = LocalStorageUtils.isLocalStorageAvailable();
        this.loader = loader;
    }

    getConfigFromCacheOrAppService(): Promise<AITrackerConfig> {
        let config: AITrackerConfig | null = null;
        if (this._localStorageWorking) {
            const configString = localStorage.getItem(cacheName);
            if (configString) {
                try {
                    config = JSON.parse(configString);
                } catch (e) {
                    debug("Failed to parse config from local storage cache, will reload from API");
                }
            }
        }
        if (config && AITrackerConfig.isValid(config)) {
            log("Config found in local storage cache");
            return Promise.resolve(config);
        }

        debug("Config not found in cache, loading from API");
        return this.loader.loadConfig().then((t: ConfigLoadResult) => {
            if (t.success) {
                log("Script config succesfully loaded from API");
                this.setConfigCache(t.config);
            }
            else {
                log("Script config not loaded from API, using default");
            }
            return t.config;
        }
        );
    }

    haveValidCachedConfig(): boolean {
        if (this._localStorageWorking) {
            const configString = localStorage.getItem(cacheName);
            if (configString) {
                try {
                    const config: AITrackerConfig = JSON.parse(configString);
                    if (config) {
                        log("Config found in cache");
                        return AITrackerConfig.isValid(config);
                    }
                    else {
                        log("Config found in cache but not valid");
                        return false;
                    }
                } catch (e) {
                    debug("Failed to parse cached config");
                    return false;
                }
            }
        }
        return false;
    }

    setConfigCache(config: AITrackerConfig): void {
        if (this._localStorageWorking) {
            localStorage.setItem('AITrackerConfig', JSON.stringify(config));
            debug("Config saved to local storage");
        }
        else {
            debug("Local storage not available, so config not saved");
        }
    }

    clearConfigCache(): void {
        if (this._localStorageWorking) {
            localStorage.removeItem('AITrackerConfig');
            debug("Config removed from local storage");
        }
        else {
            debug("Local storage not available, so config not removed");
        }
    }
}
