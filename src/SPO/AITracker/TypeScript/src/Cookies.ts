import Cookies from 'js-cookie'
import { PageStats } from './Definitions';
import { error } from './Logger';

export function GetSessionCookieVal() : string
{
    return Cookies.get("SPOInsightsSessionID") ?? '';
}
export function SetSessionCookieVal(sessionId: string)
{
    Cookies.set("SPOInsightsSessionID", sessionId);
}

// Remember last page URL in a cookie. Used so previous URL stats can be uploaded in next page nav
export function GetLastTrackedPageVal() : string
{
    return Cookies.get("SPOInsightsLastTrackedUrl") ?? '';
}
export function SetLastTrackedPageVal(url: string)
{
    Cookies.set("SPOInsightsLastTrackedUrl", url);
}

export function GetLastPageStatsVal() : PageStats | null
{
    const s = Cookies.get("SPOInsightsLastPageStats");
    if (s)
    {
        // Convert string to JSON
        try {
            const lastPageStats: PageStats = JSON.parse(s);
            return lastPageStats;
        } catch (e) {
            error("Got an error turning 'SPOInsightsLastPageStats' contents into JSON.");
            error(e);
            return null;
        }
    }
    else
        return null;
}
export function SetLastPageStatsVal(stats: PageStats)
{
    Cookies.set("SPOInsightsLastPageStats", JSON.stringify(stats), { expires: 7 });
}
export function ClearLastPageStatsVal()
{
    Cookies.remove("SPOInsightsLastPageStats");
}

export function CleanCookies() : void
{
    Cookies.remove("ai_authUser");
    Cookies.remove("ai_session");
    Cookies.remove("ai_user");
}
