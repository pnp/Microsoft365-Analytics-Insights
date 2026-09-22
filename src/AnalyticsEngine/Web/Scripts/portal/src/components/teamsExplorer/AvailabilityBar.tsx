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
import { useT, type TranslationKey } from '../../i18n';
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

  const sources: { labelKey: TranslationKey; on: boolean; detail?: string }[] = [
    { labelKey: 'teamsExplorer.availability.source.usageReports', on: availability.usageReportsAvailable },
    { labelKey: 'teamsExplorer.availability.source.calls', on: availability.callsAvailable },
    {
      labelKey: 'teamsExplorer.availability.source.teamsChannels',
      on: availability.teamsAnalyticsAvailable && availability.authorisedTeams > 0,
      detail: availability.teamsAnalyticsAvailable
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

      {availability.reasons.length > 0 && (
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
              { count: availability.reasons.length },
            )}
          </Button>

          {expanded && (
            <MessageBar intent={availability.available ? 'info' : 'warning'}>
              <MessageBarBody>
                <MessageBarTitle>{t('teamsExplorer.availability.missingTitle')}</MessageBarTitle>
                <ul className={styles.reasons}>
                  {availability.reasons.map((reason) => (
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
