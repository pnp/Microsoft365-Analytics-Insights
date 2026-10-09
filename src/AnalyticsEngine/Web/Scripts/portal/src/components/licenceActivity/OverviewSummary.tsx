import { memo } from 'react';
import { makeStyles, tokens, Card, Text } from '@fluentui/react-components';
import type { LicenceActivityAllLicences, LicenceActivitySku } from '../../types/licenceActivity';
import { useT } from '../../i18n';
import { formatCount } from './format';
import AdoptionScoreBar, { AdoptionScoreInfo } from './AdoptionScoreBar';

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
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  value: {
    color: tokens.colorNeutralForeground1,
    lineHeight: '1.1',
  },
  caption: {
    color: tokens.colorNeutralForeground3,
  },
});

interface OverviewSummaryProps {
  /** distinctAssignedUsers - people holding any licence in the current scope, counted once. */
  distinctAssignedUsers: number;
  /** Every licence type in the figures, held or not. */
  licences: LicenceActivitySku[];
  /** Everyone holding a licence; null from a server that predates it. */
  allLicences: LicenceActivityAllLicences | null;
}

/**
 * The headline strip: the shape of the selection before the detail below it - how many people hold a
 * licence, how many licence types they hold, and how much everyone holding a licence uses their
 * services (the adoption score every licence is compared with).
 */
function OverviewSummary({ distinctAssignedUsers, licences, allLicences }: OverviewSummaryProps) {
  const styles = useStyles();
  const t = useT();
  const held = licences.filter((l) => l.assignedUsers > 0).length;
  const unheld = licences.length - held;

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
          {t('licenceActivity.overview.licencesHeld')}
        </Text>
        <Text size={800} weight="bold" className={styles.value}>
          {formatCount(held)}
        </Text>
        <Text size={200} className={styles.caption}>
          {unheld > 0
            ? t('licenceActivity.overview.licencesNobodyHolds', { count: formatCount(unheld) })
            : t('licenceActivity.overview.assignedMany')}
        </Text>
      </Card>

      {allLicences && (
        <Card className={styles.card}>
          <span className={styles.label}>
            <Text size={200} weight="semibold">
              {t('licenceActivity.compare.adoptionScore')}
            </Text>
            <AdoptionScoreInfo />
          </span>
          <AdoptionScoreBar score={allLicences.adoptionScore} large />
          <Text size={200} className={styles.caption}>
            {t('licenceActivity.overview.scoreAcrossEveryone')}
          </Text>
        </Card>
      )}
    </div>
  );
}

// Memoised: the page re-renders on unrelated state (users snapshot id, export state); these figures only
// change when the scope does.
export default memo(OverviewSummary);
