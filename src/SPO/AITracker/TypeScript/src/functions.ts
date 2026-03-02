import { error } from "./Logger";

const textEncoder = new TextEncoder();

// This function will split an array into multiple arrays of a maximum byte size
export function splitIntoJsonArraysOfMaxBytes<T>(d: T[] | undefined, maxBytesSize: number, callBack: Function): void {
    let nextCallbackResults: T[] = [];
    if (d && Array.isArray(d)) {
        d.forEach((item: T, idx: number) => {
            nextCallbackResults.push(item);
    
            // Is the next item going to push us over the limit?
            if (idx < d.length - 1) {
                const arraySoFarPlusNext: T[] = [...nextCallbackResults, d[idx + 1]];
                const arraySoFarPlusNextJson = JSON.stringify(arraySoFarPlusNext);
                if (textEncoder.encode(arraySoFarPlusNextJson).length > maxBytesSize) {
                    callBack(nextCallbackResults);
                    nextCallbackResults = [];
                }
            }
        });
    }
    else {
        error("splitIntoJsonArraysOfMaxBytes: input array is undefined or not an array");
    }


    // If we have any left over, call the callback
    if (nextCallbackResults.length > 0) {
        callBack(nextCallbackResults);
    }
}