import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProvider } from '../../test/renderWithProvider';
import AvailabilityBar, { availabilityReasonTexts } from './AvailabilityBar';
import { EN_CATALOG } from '../../i18n';
import type { TFunction, TranslationKey } from '../../i18n';
import type { TeamsAvailability } from '../../types/teamsExplorer';

/**
 * The availability strip tells an administrator what is switched off, and its wording is authored
 * by the server. Translating it meant the SPA now reproduces the server's branches instead of
 * rendering its sentences - so these guard the reproduction, which is the part that can silently
 * drift from `TeamsExplorerAvailability.Build`.
 *
 * `Tests.UnitTests/TeamsExplorerAvailabilityTests.cs` guards the C# half of the same rules.
 *
 * The reasons sit behind a disclosure in the real component, so the branch logic is asserted on
 * `availabilityReasonTexts` directly. A first version of this file asserted on rendered text, and
 * every *negative* case passed for the wrong reason: nothing was rendered at all, because the
 * disclosure was shut. The rendering test below exists to stop that happening again.
 */

/** Resolves keys the way the component does, so a renamed key fails here rather than silently. */
const t = ((key: TranslationKey) => EN_CATALOG[key] ?? key) as TFunction;

function availability(): TeamsAvailability {
  return {
    usageReportsAvailable: true,
    callsAvailable: true,
    serviceBusAvailable: true,
    teamsAnalyticsAvailable: true,
    cognitiveAvailable: true,
    userMetadataAvailable: true,
    authorisedTeams: 5,
    totalTeams: 10,
    teamCountsKnown: true,
    available: true,
    reasons: [],
  };
}

const reasonsFor = (overrides: Partial<TeamsAvailability> = {}) =>
  availabilityReasonTexts({ ...availability(), ...overrides }, t);

describe('Teams Explorer availability reasons', () => {
  it('says nothing when every source is on', () => {
    expect(reasonsFor()).toEqual([]);
  });

  it('explains a switched-off import', () => {
    expect(reasonsFor({ usageReportsAvailable: false })).toEqual([
      EN_CATALOG['teamsExplorer.availability.reason.usageReportsOff'],
    ]);
  });

  /**
   * The counts arrive as plain numbers with an unreadable count collapsed to 0, so "no teams
   * discovered" and "the count query timed out" look identical without `teamCountsKnown`. Telling
   * an administrator no teams have been found, on the strength of a query that never returned,
   * sends them to re-authorise teams that are already fine.
   *
   * This is the SPA half of `UnknownTeamCountsAreNotReportedAsZeroAuthorised`.
   */
  it('does not report unknown team counts as zero teams', () => {
    expect(reasonsFor({ totalTeams: 0, authorisedTeams: 0, teamCountsKnown: false })).toEqual([]);
  });

  it('still reports a genuine zero', () => {
    expect(reasonsFor({ totalTeams: 0, authorisedTeams: 0, teamCountsKnown: true })).toEqual([
      EN_CATALOG['teamsExplorer.availability.reason.noTeamsDiscovered'],
    ]);
  });

  it('reports no authorised teams only once some have been discovered', () => {
    expect(reasonsFor({ totalTeams: 10, authorisedTeams: 0 })).toEqual([
      EN_CATALOG['teamsExplorer.availability.reason.noAuthorisedTeams'],
    ]);
  });

  /**
   * The server's chain is `if (!TeamsAnalytics) … else if (totalTeams == 0) … else if
   * (authorisedTeams == 0)`. Flattened into independent tests it would show two complaints at
   * once, each contradicting the other.
   */
  it('reports only the first applicable reason in the teams chain', () => {
    expect(reasonsFor({ teamsAnalyticsAvailable: false, totalTeams: 0, authorisedTeams: 0 })).toEqual(
      [EN_CATALOG['teamsExplorer.availability.reason.teamsAnalyticsOff']],
    );
  });

  it('reports the missing Service Bus only when calls are switched on', () => {
    expect(reasonsFor({ callsAvailable: false, serviceBusAvailable: false })).toEqual([
      EN_CATALOG['teamsExplorer.availability.reason.callsOff'],
    ]);
    expect(reasonsFor({ callsAvailable: true, serviceBusAvailable: false })).toEqual([
      EN_CATALOG['teamsExplorer.availability.reason.serviceBusMissing'],
    ]);
  });

  it('lists several independent reasons together', () => {
    expect(reasonsFor({ usageReportsAvailable: false, cognitiveAvailable: false })).toHaveLength(2);
  });
});

describe('The availability strip', () => {
  it('shows the reasons when the reader opens them', async () => {
    const user = userEvent.setup();
    renderWithProvider(
      <AvailabilityBar availability={{ ...availability(), usageReportsAvailable: false }} />,
    );

    // Proves the wiring as well as the wording: without this every assertion above could pass
    // while the component rendered none of them.
    expect(screen.queryByText(/usage reports import is switched off/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button'));
    expect(await screen.findByText(/usage reports import is switched off/)).toBeVisible();
  });

  /**
   * The badge said "off" and the hover said "0 of 0 authorised" whenever the count query failed -
   * asserting a zero nobody measured, on the one source whose whole story is how many teams are
   * authorised.
   */
  it('claims nothing about team numbers when the counts are unknown', () => {
    renderWithProvider(
      <AvailabilityBar
        availability={{
          ...availability(),
          totalTeams: 0,
          authorisedTeams: 0,
          teamCountsKnown: false,
        }}
      />,
    );

    expect(screen.queryByText(/0 of 0/)).not.toBeInTheDocument();
  });

  it('says how many teams are authorised when the counts are known', () => {
    renderWithProvider(
      <AvailabilityBar availability={{ ...availability(), totalTeams: 10, authorisedTeams: 5 }} />,
    );
    expect(screen.getByText(/5 of 10/)).toBeVisible();
  });
});
