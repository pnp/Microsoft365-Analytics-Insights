/**
 * Runs `body` as a reader in another time zone would, then puts the process's zone back.
 *
 * CI runs the suite in UTC, where reading a date-time as local time and reading it as UTC give the
 * same answer, so a date bug that every reader in Madrid sees cannot fail there. Node re-reads
 * `process.env.TZ` when it is assigned, so a test can move itself east or west of UTC.
 *
 * The zone is checked before `body` runs: a test that silently stayed in UTC would pass whether or
 * not the code it covers is right.
 */
export function inTimeZone<T>(zone: string, body: () => T): T {
  const previous = process.env.TZ;
  process.env.TZ = zone;
  try {
    const midsummer = new Date(2026, 6, 1).getTimezoneOffset();
    const midwinter = new Date(2026, 0, 1).getTimezoneOffset();
    if (zone !== 'UTC' && midsummer === 0 && midwinter === 0) {
      throw new Error(`The process did not move to ${zone}; a time-zone test here would prove nothing.`);
    }
    return body();
  } finally {
    if (previous === undefined) delete process.env.TZ;
    else process.env.TZ = previous;
  }
}
