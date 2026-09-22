import { useState } from 'react';
import {
  makeStyles,
  tokens,
  Text,
  Button,
  Badge,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
} from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import { useT, type TFunction, type TranslationKey } from '../../i18n';
import type { TeamsAvailability } from '../../types/teamsExplorer';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    marginTop: '12px',
  },
  badges: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
    alignItems: 'center',
  },
  reasons: {
    margin: '4px 0 0 0',
    paddingInlineStart: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  toggle: {
    alignSelf: 'flex-start',
  },
});

/**
 * The reasons the server would have written, rebuilt from the flags it sends.
 *
 * Exported for its own tests: this reproduces a chain of conditions that lives in C#
 * (`TeamsExplorerAvailability.Build`), and a faithful copy is the whole point - an `else if`
 * flattened into an `if` shows an administrator two complaints that contradict each other.
 */
export function availabilityReasonTexts(availability: TeamsAvailability, t: TFunction): string[] {
  const reasons: string[] = [];

  if (!availability.usageReportsAvailable) {
    reasons.push(t('teamsExplorer.availability.reason.usageReportsOff'));
  }

  if (!availability.callsAvailable) {
    reasons.push(t('teamsExplorer.availability.reason.callsOff'));
  } else if (!availability.serviceBusAvailable) {
    reasons.push(t('teamsExplorer.availability.reason.serviceBusMissing'));
  }

  if (!availability.teamsAnalyticsAvailable) {
    reasons.push(t('teamsExplorer.availability.reason.teamsAnalyticsOff'));
  } else if (availability.teamCountsKnown && availability.totalTeams === 0) {
    // Gated on teamCountsKnown, mirroring the server's `totalTeams.HasValue` guard. The counts are
    // plain numbers on the wire with null collapsed to 0, so without the flag a count the store
    // could not read - its query times out on a large tenant - would be reported to the
    // administrator as "no teams have been discovered yet". Unknown is not zero.
    reasons.push(t('teamsExplorer.availability.reason.noTeamsDiscovered'));
  } else if (availability.teamCountsKnown && availability.authorisedTeams === 0) {
    reasons.push(t('teamsExplorer.availability.reason.noAuthorisedTeams'));
  }

  if (!availability.cognitiveAvailable) {
    reasons.push(t('teamsExplorer.availability.reason.cognitiveMissing'));
  }

  if (!availability.userMetadataAvailable) {
    reasons.push(t('teamsExplorer.availability.reason.userMetadataOff'));
  }

  return reasons;
}

/**
 * The per-source status strip.
 *
 * Every source gets a badge whether or not it is on, and the reasons are one click away rather than
 * hidden. Hiding an unavailable tab - the obvious alternative - tells an admin nothing: they are
 * left wondering whether the feature exists, whether the import is broken, or whether their tenant
 * simply has no Teams activity. Naming the missing toggle turns "why is this empty?" into a task.
 */
export default function AvailabilityBar({ availability }: { availability: TeamsAvailability }) {
  const styles = useStyles();
  const t = useT();
  const [expanded, setExpanded] = useState(false);
  const reasons = availabilityReasonTexts(availability, t);

  const sources: { labelKey: TranslationKey; on: boolean; detail?: string }[] = [
    { labelKey: 'teamsExplorer.availability.source.usageReports', on: availability.usageReportsAvailable },
    { labelKey: 'teamsExplorer.availability.source.calls', on: availability.callsAvailable },
    {
      labelKey: 'teamsExplorer.availability.source.teamsChannels',
      // An unknown count must not be read as "none authorised": the badge would say the source is
      // off, and the detail would assert "0 of 0 authorised", on the strength of a query that
      // never returned. With the count unknown, report what is actually known - whether the
      // import is switched on - and say nothing about how many teams there are.
      on:
        availability.teamsAnalyticsAvailable &&
        (!availability.teamCountsKnown || availability.authorisedTeams > 0),
      detail:
        availability.teamsAnalyticsAvailable && availability.teamCountsKnown
          ? t('teamsExplorer.availability.authorisedTeams', {
            authorised: availability.authorisedTeams,
            total: availability.totalTeams,
          })
          : undefined,
    },
    { labelKey: 'teamsExplorer.availability.source.cognitiveEnrichment', on: availability.cognitiveAvailable },
    { labelKey: 'teamsExplorer.availability.source.userDemographics', on: availability.userMetadataAvailable },
  ];

  return (
    <div className={styles.root}>
      <div className={styles.badges}>
        <Text size={200} className={styles.muted}>
          {t('teamsExplorer.availability.dataSources')}
        </Text>
        {sources.map((source) => (
          <Badge
            key={source.labelKey}
            appearance="tint"
            color={source.on ? 'success' : 'informative'}
            title={source.detail}
          >
            {t(source.labelKey)}
            {source.detail ? ` \u2013 ${source.detail}` : ''}
          </Badge>
        ))}
      </div>

      {reasons.length > 0 && (
        <>
          <Button
            className={styles.toggle}
            appearance="subtle"
            size="small"
            icon={expanded ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
            onClick={() => setExpanded((open) => !open)}
            aria-expanded={expanded}
          >
            {t(
              expanded
                ? 'teamsExplorer.availability.hideMissing'
                : 'teamsExplorer.availability.showMissing',
              { count: reasons.length },
            )}
          </Button>

          {expanded && (
            <MessageBar intent={availability.available ? 'info' : 'warning'}>
              <MessageBarBody>
                <MessageBarTitle>{t('teamsExplorer.availability.missingTitle')}</MessageBarTitle>
                <ul className={styles.reasons}>
                  {reasons.map((reason) => (
                    <li key={reason}>
                      <Text size={200}>{reason}</Text>
                    </li>
                  ))}
                </ul>
              </MessageBarBody>
            </MessageBar>
          )}
        </>
      )}
    </div>
  );
}
