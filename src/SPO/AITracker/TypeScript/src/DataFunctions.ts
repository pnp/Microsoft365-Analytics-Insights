
import { SpoPerfJson } from "./Definitions";
import { debug, warn } from "./Logger";

// RFC4122 version 4 compliant GUID generator.
// From https://stackoverflow.com/questions/105034/create-guid-uuid-in-javascript
export function uuidv4() : string {
    var uuid = require("uuid");
    return uuid.v4();
}

// Gets SPRequestDuration from a string if it can find it.
export function getSPRequestDuration(source: string) : number | null {

    // Find:
    //      "perf":{ .... }
    // This in reality is an unsupported way of measuring the performance. If it stops working it could be these stats are packaged in a different way.
    const PERF_BLOCK_START = `"perf":{"`, PERF_BLOCK_END = "},";

    var perfJsonStart = source.indexOf(PERF_BLOCK_START);
    if (perfJsonStart > -1) {
        var sourceSliceOuter = source.substring(perfJsonStart, source.length);
        var perfJsonEnd = sourceSliceOuter.indexOf(PERF_BLOCK_END);

        if (perfJsonEnd > -1) {
            var perfJsonWithPropName = sourceSliceOuter.substring(0, perfJsonEnd + PERF_BLOCK_END.length);
            var perfJson = perfJsonWithPropName.substring(PERF_BLOCK_START.length - 2, perfJsonWithPropName.length);

            // Cleam trailing coma if there is one
            if (perfJson.endsWith(",")) perfJson = perfJson.substring(0, perfJson.length - 1);

            // SPO for some reason sometimes inserts "\r" into the JSon object. Remove "\r" literals.
            perfJson = perfJson.split('\\r').join('');

            // Parse JSon and extract vars
            var perfJsonBlock: SpoPerfJson | null = null;
            try {
                perfJsonBlock= JSON.parse(perfJson);
            } catch (error) {
                warn("Parsing error converting performance data string into JSon. Original text: " + perfJson);
            }

            // Output
            if (perfJsonBlock) {
                debug(`Extracted spRequestDuration: ${perfJsonBlock.spRequestDuration}`);
                return perfJsonBlock.spRequestDuration;
            }

        }
    }
    return null;
}

export function isValidGuid(str : string | null) : boolean
{

    if (!str) {
        return false;
    }
    var uuid = require("uuid");

    return uuid.validate(str);
}

// The link's text. A link with none, such as an icon or image link, by its accessible label instead: the importer skips a
// click without a label, so clicks on SharePoint's own icon links (the site logo, the header home link) were all lost.
export function linkLabel(a: HTMLAnchorElement): string {
    if (a.text && a.text.trim() !== '') {
        return a.text;      // As it always was, so labels already stored still match
    }
    const img = a.querySelector('img[alt]');
    return a.getAttribute('aria-label') || a.title || (img && img.getAttribute('alt')) || '';
}

// Whether two URLs are the same page: the same address apart from the #fragment, which an in-page link changes. The query
// counts: it's how the tracker tells pages apart everywhere else, and on a search page it is the search.
export function isSamePage(url1: string, url2: string): boolean {
    const page = (url: string) => (url || '').split('#')[0];
    return page(url1) === page(url2);
}
