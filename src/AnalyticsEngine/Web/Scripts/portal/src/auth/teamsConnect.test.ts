import { describe, it, expect } from 'vitest';
import {
  TEAMS_CONNECT_OUTCOME_KEYS,
  readTeamsConnectOutcome,
  withoutTeamsConnectOutcome,
} from './teamsConnect';

const read = (query: string) => readTeamsConnectOutcome(new URLSearchParams(query));

describe('readTeamsConnectOutcome', () => {
  it('is null when the page was not reached from a failed connection', () => {
    expect(read('')).toBeNull();
    expect(read('teamsConnectError=AADSTS65001')).toBeNull();
    expect(read('teamsConnect=')).toBeNull();
  });

  it('maps each outcome the server sends to its explanation', () => {
    expect(read('teamsConnect=consent_required&teamsConnectError=AADSTS65001')).toEqual({
      messageKey: TEAMS_CONNECT_OUTCOME_KEYS.consent_required,
      errorCode: 'AADSTS65001',
    });
    expect(read('teamsConnect=access_denied')).toEqual({
      messageKey: TEAMS_CONNECT_OUTCOME_KEYS.access_denied,
      errorCode: null,
    });
    expect(read('teamsConnect=failed&teamsConnectError=invalid_grant')).toEqual({
      messageKey: TEAMS_CONNECT_OUTCOME_KEYS.failed,
      errorCode: 'invalid_grant',
    });
  });

  it('reads an unknown outcome as a failure, including names every object inherits', () => {
    for (const outcome of ['something_new', 'constructor', '__proto__', 'toString']) {
      expect(read(`teamsConnect=${outcome}`)?.messageKey, outcome).toBe(TEAMS_CONNECT_OUTCOME_KEYS.failed);
    }
  });

  it('drops an error code that is not a plain identifier', () => {
    for (const code of ['<b>x</b>', 'AADSTS 65001', 'javascript:alert(1)', 'A'.repeat(41)]) {
      expect(read(`teamsConnect=failed&teamsConnectError=${encodeURIComponent(code)}`)?.errorCode, code).toBeNull();
    }
  });
});

describe('withoutTeamsConnectOutcome', () => {
  it('removes only the connection outcome', () => {
    const next = withoutTeamsConnectOutcome(
      new URLSearchParams('teamsConnect=failed&teamsConnectError=AADSTS65001&keep=1'),
    );
    expect(next?.toString()).toBe('keep=1');
  });

  it('is null when there is nothing to remove, so the page does not rewrite the URL for nothing', () => {
    expect(withoutTeamsConnectOutcome(new URLSearchParams('keep=1'))).toBeNull();
  });
});
