import { Log } from '@microsoft/sp-core-library';

const LOG_SOURCE: string = "SPOInsights ModernUI";

/**
 * Unified logger that writes every message to both the browser console
 * AND the SPFx Log infrastructure, so messages are visible in production
 * console regardless of SPFx log-level filtering.
 */
export class Logger {

  public static verbose(message: string): void {
    console.debug(`[${LOG_SOURCE}] ${message}`);
    Log.verbose(LOG_SOURCE, message);
  }

  public static info(message: string): void {
    console.info(`[${LOG_SOURCE}] ${message}`);
    Log.info(LOG_SOURCE, message);
  }

  public static warn(message: string): void {
    console.warn(`[${LOG_SOURCE}] ${message}`);
    Log.warn(LOG_SOURCE, message);
  }

  public static error(message: string): void {
    console.error(`[${LOG_SOURCE}] ${message}`);
    Log.error(LOG_SOURCE, new Error(message));
  }
}
