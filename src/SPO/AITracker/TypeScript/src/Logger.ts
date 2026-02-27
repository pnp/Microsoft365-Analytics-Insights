
export const LOGGING_PREFIX = "SPOInsights AI Tracker: ";
export function log(msg: string) {
    console.log(LOGGING_PREFIX + msg);
}

export function error(msg: any) {
    if (typeof msg === 'string') {
        console.error(LOGGING_PREFIX + msg);
    } else {
        console.error(LOGGING_PREFIX, msg);
    }
}

export function warn(msg: string) {
    console.warn(LOGGING_PREFIX + msg);
}
export function debug(msg: string) {
    console.debug(LOGGING_PREFIX + msg);
}

/** Log a debug-level object dump with consistent prefix */
export function debugObj(label: string, obj: any) {
    console.debug(LOGGING_PREFIX + label, obj);
}
