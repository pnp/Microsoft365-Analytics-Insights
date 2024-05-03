
import { SpoPerfJson } from "./Definitions";
import { warn } from "./Logger";

// RFC4122 version 4 compliant GUID generator.
// From https://stackoverflow.com/questions/105034/create-guid-uuid-in-javascript
export function uuidv4() : string {
    var uuid = require("uuid");
    return uuid.v4();
}

// Gets SPRequestDuration from a string if it can find it.
export function getSPRequestDuration(source) : number | null {

    // Find:
    //      "perf":{ .... }
    // This in reality is an unsupported way of measuring the performance. If it stops working it could be these stats are packaged in a different way.
    const PERF_BLOCK_START = `"perf":{"`, PERF_BLOCK_END = "},";

    var perfJsonStart = source.search(PERF_BLOCK_START);
    if (perfJsonStart > -1) {
        var sourceSliceOuter = source.substring(perfJsonStart, source.length);
        var perfJsonEnd = sourceSliceOuter.search(PERF_BLOCK_END);

        if (perfJsonEnd > -1) {
            var perfJsonWithPropName = sourceSliceOuter.substring(0, perfJsonEnd + PERF_BLOCK_END.length);
            var perfJson = perfJsonWithPropName.substring(PERF_BLOCK_START.length - 1, perfJsonWithPropName.length);

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
