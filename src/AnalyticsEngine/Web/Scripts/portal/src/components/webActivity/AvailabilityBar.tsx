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
import {
  ChevronDown16Regular,
  ChevronRight16Regular,
  CheckmarkCircle16Regular,
  Dismiss16Regular,
  Question16Regular,
} from '@fluentui/react-icons';
import type { WebActivityAvailability } from '../../types/webActivity';
import { formatDate } from './webActivityShared';

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
 * left wondering whether the feature exists, whether the tracker is broken, or whether their
 * intranet genuinely has no traffic. Naming the missing piece turns "why is this empty?" into a task.
 *
 * Each badge spells out "on" or "off" in words as well as in colour. Colour alone fails WCAG 1.4.1
 * and, more practically, is unreadable to a colour-blind admin and invisible to a screen reader -
 * on a strip whose entire purpose is to say which sources are working.
 *
 * The "last page view" badge is the single most useful thing here. A tracker that was working and
 * stopped looks exactly like a tracker that was never deployed, until you see the date.
 */
export default function AvailabilityBar({ availability }: { availability: WebActivityAvailability }) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);

  const sources: { label: string; on: boolean; unknown?: boolean; detail?: string }[] = [
    { label: 'Web traffic import', on: availability.webTrafficAvailable },
    { label: 'Application Insights', on: availability.appInsightsConfigured },
    {
      label: 'Page views collected',
      on: availability.hasAnyHits,
      // "Nothing collected" and "the check failed" need opposite advice, so the badge has to be
      // able to say it does not know - otherwise it reads "off" beside a message saying the
      // opposite, which is how an admin ends up redeploying a working tracker.
      unknown: !availability.collectionStatusKnown,
      detail: availability.lastHitUtc ? `last ${formatDate(availability.lastHitUtc)}` : undefined,
    },
    { label: 'Search terms', on: availability.searchAvailable },
    { label: 'Element clicks', on: availability.clickTrackingAvailable },
    { label: 'User directory', on: availability.userMetadataAvailable },
  ];

  return (
    <div className={styles.root}>
      <div className={styles.badges}>
        <Text size={200} className={styles.muted}>
          Data sources:
        </Text>
        {sources.map((source) => (
          <Badge
            key={source.label}
            appearance="tint"
            color={source.unknown ? 'warning' : source.on ? 'success' : 'informative'}
            icon={
              source.unknown ? (
                <Question16Regular />
              ) : source.on ? (
                <CheckmarkCircle16Regular />
              ) : (
                <Dismiss16Regular />
              )
            }
            title={source.detail}
          >
            {source.label}: {source.unknown ? 'unknown' : source.on ? 'on' : 'off'}
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
            {expanded ? 'Hide' : 'Show'} what is missing ({availability.reasons.length})
          </Button>

          {expanded && (
            <MessageBar intent={availability.available ? 'info' : 'warning'}>
              <MessageBarBody>
                <MessageBarTitle>Some web traffic data is not being collected</MessageBarTitle>
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
