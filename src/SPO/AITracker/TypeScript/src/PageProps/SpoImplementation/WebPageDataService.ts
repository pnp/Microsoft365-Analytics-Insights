import { AppInsightsWrapper } from "../../AppInsightsWrapper";
import { IPageDataService } from "../../Definitions";
import { PageProps } from "../Models/PageProps";

export class WebPageDataService implements IPageDataService
{
    _ai: AppInsightsWrapper;

    constructor(appInsightsWrapper : AppInsightsWrapper) {
        this._ai = appInsightsWrapper;
    }
    setPageUpdateInterval(interval: number): void {
        throw new Error("Method not implemented.");
    }

    recordPageProps(props: PageProps): void {
        this._ai.updatePageProps(props);
    }
}
