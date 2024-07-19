import { ObjectWithExpiry } from "./Definitions";

export class AITrackerConfig implements ObjectWithExpiry {

    metadataRefreshMinutes: number;
    expiry: Date;

    constructor(metadataRefreshMinutes: number, expiry: Date) {
        this.metadataRefreshMinutes = metadataRefreshMinutes;
        this.expiry = expiry;
    }
    static GetDefault(): AITrackerConfig {

        const twentyFourHoursInMinutes = 24 * 60;
        const twentyFourHoursFromNow = new Date();

        twentyFourHoursFromNow.setMinutes(twentyFourHoursFromNow.getMinutes() + twentyFourHoursInMinutes);
        return new AITrackerConfig(twentyFourHoursInMinutes, twentyFourHoursFromNow);
    }

    static isValid(c: AITrackerConfig): boolean {
        
        const expiry = new Date(c.expiry);
        return c.metadataRefreshMinutes > 0 && new Date() < expiry;
    }
}
