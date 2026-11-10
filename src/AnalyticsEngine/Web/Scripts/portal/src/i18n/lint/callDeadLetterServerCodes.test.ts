// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { EN_CATALOG } from '../catalog';
import {
  DEAD_LETTER_REASON_KEYS,
  FAILURE_CODE_KEYS,
  SKIP_CODE_KEYS,
  UNAVAILABLE_REASON_KEYS,
} from '../../components/callDeadLetters/CallDeadLettersCard';
import { CALL_DEAD_LETTER_ERROR_KEYS } from '../../api/callDeadLettersApi';

/**
 * The dead-lettered Teams calls card writes every sentence itself from codes the server sends: the importer's
 * dead-letter reasons, and the request contract's failure, skip and availability codes. A code the server can send that
 * the card doesn't map would be shown to a Spanish reader as a bare English code, so this reads the C# and fails when
 * the two drift - in either direction.
 */
const ENGINE = join(process.cwd(), '..', '..', '..');

function read(...path: string[]): string {
  return readFileSync(join(ENGINE, ...path), 'utf8');
}

/** The values of a static class's <c>public const string</c> fields. */
function constantsOf(source: string, className: string): string[] {
  const body = source.match(new RegExp(`public static class ${className}\\s*\\{([\\s\\S]*?)\\n    \\}`))?.[1];
  if (!body) throw new Error(`No static class ${className}.`);
  return [...body.matchAll(/public const string \w+ = "([^"]+)";/g)].map((m) => m[1]).sort();
}

const sorted = (values: Iterable<string>) => [...values].sort();

describe('Dead-lettered Teams calls: codes the server sends', () => {
  it('names every dead-letter reason the importer gives, and Service Bus its own', () => {
    const sources = [
      read('WebJob.Office365ActivityImporter.Engine', 'Graph', 'Calls', 'CallQueueMessageHandler.cs'),
      read('WebJob.Office365ActivityImporter.Engine', 'Graph', 'Calls', 'CallQueueRetryPolicy.cs'),
      read('WebJob.Office365ActivityImporter.Engine', 'Entities', 'Serialisation', 'CallRecord', 'CallRecordPayloadTypes.cs'),
    ].join('\n');
    const importer = [...sources.matchAll(/public const string \w+Reason\s*=\s*"([^"]+)";/g)].map((m) => m[1]);
    expect(importer.length).toBeGreaterThanOrEqual(7);

    const unmapped = [...importer, 'MaxDeliveryCountExceeded'].filter((reason) => !DEAD_LETTER_REASON_KEYS.has(reason));
    expect(
      unmapped,
      'The importer dead-letters calls with these reasons, but the card has no text for them. Add each to\n' +
        'DEAD_LETTER_REASON_KEYS and to src/i18n/catalog/{en,es}/callDeadLetters.ts:\n  ' +
        unmapped.join('\n  '),
    ).toEqual([]);
  });

  it('maps every failure, skip and availability code of the request contract, and no code it no longer has', () => {
    const contract = read('Common', 'Entities', 'Calls', 'CallDeadLetterRequest.cs');

    expect(sorted(FAILURE_CODE_KEYS.keys())).toEqual(constantsOf(contract, 'CallDeadLetterFailureCodes'));
    expect(sorted(SKIP_CODE_KEYS.keys())).toEqual(constantsOf(contract, 'CallDeadLetterSkipCodes'));
    expect(sorted(UNAVAILABLE_REASON_KEYS.keys())).toEqual(constantsOf(contract, 'CallDeadLetterUnavailableReasons'));
  });

  it('maps every code api/CallDeadLetters refuses a request with', () => {
    const contract = read('Common', 'Entities', 'Calls', 'CallDeadLetterRequest.cs');
    const models = read('Web', 'Models', 'Calls', 'CallDeadLetterModels.cs');

    expect(sorted(CALL_DEAD_LETTER_ERROR_KEYS.keys())).toEqual(
      sorted([...constantsOf(models, 'CallDeadLetterErrorCodes'), ...constantsOf(contract, 'CallDeadLetterUnavailableReasons')]),
    );
  });

  it('points every code at a catalog entry', () => {
    const keys = [DEAD_LETTER_REASON_KEYS, FAILURE_CODE_KEYS, SKIP_CODE_KEYS, UNAVAILABLE_REASON_KEYS, CALL_DEAD_LETTER_ERROR_KEYS]
      .flatMap((map) => [...map.values()]);
    expect(keys.filter((key) => !(key in EN_CATALOG))).toEqual([]);
  });
});
