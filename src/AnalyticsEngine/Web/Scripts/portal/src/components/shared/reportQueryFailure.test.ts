import { afterEach, beforeAll, describe, expect, it } from 'vitest';

import { loadCatalog } from '../../i18n/catalog';
import { setActiveLanguage, translateStatic, type TFunction } from '../../i18n';
import { reportQueryFailureText } from './reportQueryFailure';

const tEs: TFunction = (key, values) => translateStatic('es', key, values);
const tEn: TFunction = (key, values) => translateStatic('en', key, values);
const reference = '00000000-0000-0000-0000-000000000002';

beforeAll(async () => {
  await loadCatalog('es');
});

afterEach(() => setActiveLanguage('en'));

describe('reportQueryFailureText', () => {
  it('words the reason and keeps the reference, in the reader\'s language', () => {
    expect(reportQueryFailureText(tEn, 'Unavailable', reference, 'server English'))
      .toBe(`the database was not available (error reference ${reference})`);
    expect(reportQueryFailureText(tEs, 'Unavailable', reference, 'server English'))
      .toBe(`la base de datos no estaba disponible (referencia de error ${reference})`);
  });

  it('shows the server\'s English for a reason this build does not know, or a response without one', () => {
    expect(reportQueryFailureText(tEs, 'SomethingNew', reference, 'server English')).toBe('server English');
    expect(reportQueryFailureText(tEs, null, null, 'older server text')).toBe('older server text');
    expect(reportQueryFailureText(tEs, 'Timeout', undefined, 'server English')).toBe('server English');
  });
});
