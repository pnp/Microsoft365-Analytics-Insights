import { AITrackerConfig } from "../Models";

export interface IConfigLoader {
    loadConfig(): Promise<AITrackerConfig>;
}
