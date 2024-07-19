import { AITrackerConfig } from "../Models";

export interface IConfigLoader {
    loadConfig(): Promise<ConfigLoadResult>;
}

export interface ConfigLoadResult {
    config: AITrackerConfig;
    success: boolean;
}