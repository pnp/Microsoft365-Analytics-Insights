import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProvider } from '../../test/renderWithProvider';
import AvailabilityBar, { availabilityReasonTexts } from './AvailabilityBar';
import { EN_CATALOG } from '../../i18n';
import type { TFunction, TranslationKey } from '../../i18n';
import type { WebActivityAvailability } from '../../types/webActivity';

const t = ((key: TranslationKey, vars?: Record<string, string>) => {
  let value = EN_CATALOG[key] ?? key;
  for (const [name, replacement] of Object.entries(vars ?? {})) {
    value = value.replace(`{${name}}`, replacement);
  }
  return value;
}) as TFunction;

function availability(): WebActivityAvailability {
  return {
    webTrafficAvailable: true,
    userMetadataAvailable: true,
    appInsightsConfigured: true,
    hasAnyHits: true,
    lastHitUtc: '2026-09-21T00:00:00Z',
    searchAvailable: true,
    clickTrackingAvailable: true,
    collectionStatusKnown: true,
    available: true,
    reasons: [],
  };
}

const reasonsFor = (overrides: Partial<WebActivityAvailability> = {}, nowUtc = new Date('2026-09-22T00:00:00Z')) =>
  availabilityReasonTexts({ ...availability(), ...overrides }, t, nowUtc);

describe('Web Activity availability reasons', () => {
  it('says nothing when every source is on and recent', () => {
    expect(reasonsFor()).toEqual([]);
  });

  it('does not treat an unknown page-hit check as zero page views', () => {
    expect(reasonsFor({ hasAnyHits: false, lastHitUtc: null, collectionStatusKnown: false })).toEqual([
      EN_CATALOG['webActivity.availability.reason.pageViewCheckFailed'],
    ]);
  });

  it('reports a genuine zero page-hit result differently from an unknown check', () => {
    expect(reasonsFor({ hasAnyHits: false, lastHitUtc: null, collectionStatusKnown: true })).toEqual([
      EN_CATALOG['webActivity.availability.reason.noPageViewsKnown'],
    ]);
  });

  it('uses the existing-hit web-traffic-off wording only when there is a last hit', () => {
    expect(reasonsFor({ webTrafficAvailable: false, lastHitUtc: '2026-09-21T00:00:00Z' })).toEqual([
      EN_CATALOG['webActivity.availability.reason.webTrafficOffWithExistingHits'],
    ]);
    expect(reasonsFor({ webTrafficAvailable: false, lastHitUtc: null, hasAnyHits: false })).toEqual([
      EN_CATALOG['webActivity.availability.reason.webTrafficOffNoHits'],
    ]);
  });

  it('uses the stale collection boundary without calling two days stale stopped', () => {
    expect(reasonsFor({ lastHitUtc: '2026-09-20T00:00:00Z' }, new Date('2026-09-22T00:00:00Z'))).toEqual([]);
    expect(reasonsFor({ lastHitUtc: '2026-09-19T00:00:00Z' }, new Date('2026-09-22T00:00:00Z'))).toEqual([
      'The most recent page view is 3 days old (19 Sept 2026). Collection appears to have stopped - check the importer on the Service health page, and confirm the Application Insights resource still has data retained for the period you are asking about.',
    ]);
  });
});

describe('The Web Activity availability strip', () => {
  it('renders catalogued reasons when opened', async () => {
    const user = userEvent.setup();
    renderWithProvider(
      <AvailabilityBar availability={{ ...availability(), userMetadataAvailable: false }} />,
    );

    expect(screen.queryByText(/Graph user metadata import is switched off/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button'));
    expect(await screen.findByText(/Graph user metadata import is switched off/)).toBeVisible();
  });
});
