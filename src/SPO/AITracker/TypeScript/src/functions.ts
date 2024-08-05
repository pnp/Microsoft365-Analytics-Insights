

// This function will split an array into multiple arrays of a maximum byte size
export function splitIntoJsonArraysOfMaxBytes<T>(d: T[] | undefined, maxBytesSize: number, callBack: Function): void {
    const textEncoder = new TextEncoder();

    let nextCallbackResults: T[] = [];
    if (d && Array.isArray(d)) {
        d.forEach((item: T, idx: number) => {
            nextCallbackResults.push(item);
    
            // Is the next item going to push us over the limit?
            if (idx < d.length - 1) {
                const arraySoFarPlusOne: T[] = [...nextCallbackResults, item];
                const arraySoFarPlusOneJson = JSON.stringify(arraySoFarPlusOne);
                if (textEncoder.encode(arraySoFarPlusOneJson).length > maxBytesSize) {
                    callBack(nextCallbackResults);
                    nextCallbackResults = [];
                }
            }
        });
    }
    else {
        console.error("splitIntoJsonArraysOfMaxBytes: input array is undefined or not an array");
    }


    // If we have any left over, call the callback
    if (nextCallbackResults.length > 0) {
        callBack(nextCallbackResults);
    }
}