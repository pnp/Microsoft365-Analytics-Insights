import { memo } from 'react';
import { makeStyles, tokens, Card, Text } from '@fluentui/react-components';
import type { LicenceActivitySku } from '../../types/licenceActivity';
import { useT } from '../../i18n';
import { formatCount, licenceName } from './format';

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
    gap: '12px',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '14px 16px',
  },
  label: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  value: {
    color: tokens.colorNeutralForeground1,
    lineHeight: '1.1',
  },
  valueMuted: {
    color: tokens.colorNeutralForeground2,
    lineHeight: '1.2',
  },
  caption: {
    color: tokens.colorNeutralForeground3,
  },
});

interface OverviewSummaryProps {
  /** distinctAssignedUsers - people holding any licence in the current scope, counted once. */
  distinctAssignedUsers: number;
  /** How many licence SKUs are in scope. */
  licenceCount: number;
  /** The licence currently drilled into, or null when none is chosen yet. */
  selectedLicence: LicenceActivitySku | null;
}

/**
 * The headline strip for the Overview tab: a handful of at-a-glance figures so the reader gets the
 * shape of the selection before the detailed tables below.
 *
 * Every value here is a figure the report already computes - the distinct assigned-user count, the
 * number of licence types, and the selected licence's own assigned count. Nothing is blended across
 * services into a single "productivity" number, deliberately: that is the same false comparison the
 * separate per-service charts avoid.
 */
function OverviewSummary({ distinctAssignedUsers, licenceCount, selectedLicence }: OverviewSummaryProps) {
  const styles = useStyles();
  const t = useT();

  return (
    <div className={styles.grid}>
      <Card className={styles.card}>
        <Text size={200} weight="semibold" className={styles.label}>
          {t('licenceActivity.overview.peopleWithLicence')}
        </Text>
        <Text size={800} weight="bold" className={styles.value}>
          {formatCount(distinctAssignedUsers)}
        </Text>
        <Text size={200} className={styles.caption}>
          {t('licenceActivity.overview.countingEachPersonOnce')}
        </Text>
      </Card>

      <Card className={styles.card}>
        <Text size={200} weight="semibold" className={styles.label}>
          {t('licenceActivity.overview.licenceTypes')}
        </Text>
        <Text size={800} weight="bold" className={styles.value}>
          {formatCount(licenceCount)}
        </Text>
        <Text size={200} className={styles.caption}>
          {licenceCount === 1 ? t('licenceActivity.overview.assignedOne') : t('licenceActivity.overview.assignedMany')}
        </Text>
      </Card>

      <Card className={styles.card}>
        <Text size={200} weight="semibold" className={styles.label}>
          {t('licenceActivity.overview.selectedLicence')}
        </Text>
        {selectedLicence ? (
          <>
            <Text size={500} weight="bold" className={styles.valueMuted}>
              {licenceName(selectedLicence)}
            </Text>
            <Text size={200} className={styles.caption}>
              {t('licenceActivity.overview.peopleHoldItSeeTabs', { count: formatCount(selectedLicence.assignedUsers) })}
            </Text>
          </>
        ) : (
          <>
            <Text size={500} weight="bold" className={styles.valueMuted}>
              {t('licenceActivity.overview.noneChosen')}
            </Text>
            <Text size={200} className={styles.caption}>
              {t('licenceActivity.overview.chooseLicenceBelow')}
            </Text>
          </>
        )}
      </Card>
    </div>
  );
}

// Memoised: the page re-renders on unrelated state (users snapshot id, export state); these three
// figures only change when the scope or the selected licence does.
export default memo(OverviewSummary);
