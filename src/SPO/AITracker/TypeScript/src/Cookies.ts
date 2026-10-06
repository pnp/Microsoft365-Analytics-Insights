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

// Cookies this script no longer writes. Removed whenever it starts, not only in a new session: they were session cookies, so
// they last until the browser closes - and in a browser that restores its last session, longer.
export function RemoveRetiredCookies() : void
{
    Cookies.remove("SPOInsightsLastTrackedUrl");    // Written by AITracker before 1.6.0; sent with every request to SharePoint
}
