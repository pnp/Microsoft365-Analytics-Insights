
export function splitByIntoJsonArraysOfMaxBytes<T>(d: T[], maxBytesSize: number, callBack: Function): void {
    const textEncoder = new TextEncoder();

    let nextCallbackResults: T[] = [];
    d.forEach((item: T, idx: number) => {
        nextCallbackResults.push(item);

        // Is the next item going to push us over the limit?
        if (idx < d.length - 1) {
            const arraySoFarPlusOne : T[] = [...nextCallbackResults, item];
            const arraySoFarPlusOneJson = JSON.stringify(arraySoFarPlusOne);
            if (textEncoder.encode(arraySoFarPlusOneJson).length > maxBytesSize) {
                callBack(nextCallbackResults);
                nextCallbackResults = [];
            }
        }
    });

    // If we have any left over, call the callback
    if (nextCallbackResults.length > 0) {
        callBack(nextCallbackResults);
    }
}
