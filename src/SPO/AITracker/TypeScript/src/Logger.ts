
export const LOGGING_PREFIX = "SPOInsights AI Tracker: ";
export function log(msg: string) {
    console.log(LOGGING_PREFIX + msg);
}

export function error(msg: string) {
    console.error(LOGGING_PREFIX + msg);
}

export function warn(msg: string) {
    console.warn(LOGGING_PREFIX + msg);
}
export function debug(msg: string) {
    console.debug(LOGGING_PREFIX + msg);
}
