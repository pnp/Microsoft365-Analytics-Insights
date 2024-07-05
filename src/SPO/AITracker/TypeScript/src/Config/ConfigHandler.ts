import { LocalStorageUtils } from "../LocalStorageUtils";
import { debug, log } from "../Logger";
import { AITrackerConfig } from "../Models";
import { IConfigLoader } from "./interfaces";

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
            const configString = localStorage.getItem('AITrackerConfig');
            if (configString) {
                config = JSON.parse(configString);
            }
        }
        if (config && AITrackerConfig.isValid(config)) {
            return Promise.resolve(config);
        }

        debug("Config not found in cache, loading from API");
        return this.loader.loadConfig().then((t: AITrackerConfig) => {
            debug("Config loaded from API");
            this.setConfigCache(t);
            return t;
        }
        );
    }

    haveValidCachedConfig(): boolean {
        if (this._localStorageWorking) {
            const configString = localStorage.getItem('AITrackerConfig');
            if (configString) {
                if (configString) {
                    const config : AITrackerConfig = JSON.parse(configString);
                    console.log(config);
                    
                    if (config) {
                        log("Config found in cache");
                        return AITrackerConfig.isValid(config);
                    }
                    else {
                        log("Config found in cache but not valid");
                        return false;
                    }
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
