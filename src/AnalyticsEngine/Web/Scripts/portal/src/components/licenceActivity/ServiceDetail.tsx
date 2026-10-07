import { memo, useEffect, useMemo, useState } from 'react';
import { makeStyles, tokens, Button, Card, Text } from '@fluentui/react-components';
import { People16Regular } from '@fluentui/react-icons';
import type { LicenceActivityOverview } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { useT } from '../../i18n';
import WorkloadDistributions, { type DistributionComparison } from './WorkloadDistributions';
import LicenceScopePicker from './LicenceScopePicker';
import AdoptionScoreBar, { AdoptionScoreInfo } from './AdoptionScoreBar';
import { formatCount, formatScore, licenceName } from './format';
import {
  ALL_LICENCES,
  scopeAssignedUsers,
  scopeLicence,
  scopeScore,
  scopeWorkloads,
  scoreRank,
  workloadRank,
  type LicenceScope,
  type Rank,
} from './scope';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '14px',
  },
  head: {
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'space-between',
    gap: '16px',
    flexWrap: 'wrap',
  },
  pickers: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: '16px',
    flexWrap: 'wrap',
  },
  summary: {
    display: 'flex',
    alignItems: 'center',
    gap: '24px',
    flexWrap: 'wrap',
  },
  figure: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  figureLabel: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

interface ServiceDetailProps {
  overview: LicenceActivityOverview;
  scope: LicenceScope;
  onScopeChange: (scope: LicenceScope) => void;
  /** Opens the People tab for the same licence. Omitted for a reader without See PII. */
  onShowPeople?: () => void;
}

/**
 * The selected population's services, directly under the licence table so choosing a licence shows its
 * detail in place: how many people hold it, its adoption score against everyone holding a licence, and
 * one card per service read against a comparison - everyone holding a licence by default, or any other
 * licence - with where the licence ranks among all the licences people hold.
 */
function ServiceDetail({ overview, scope, onScopeChange, onShowPeople }: ServiceDetailProps) {
  const styles = useStyles();
  const t = useT();
  const [compareWith, setCompareWith] = useState<LicenceScope>(ALL_LICENCES);

  const licence = scopeLicence(overview, scope);
  const everyoneName = t('licenceActivity.scope.allLicensedUsers');
  const name = licence ? licenceName(licence) : everyoneName;

  // A licence is never compared with itself; if the selection lands on the comparison, fall back to everyone.
  useEffect(() => {
    if (compareWith !== ALL_LICENCES && (compareWith === scope || !scopeLicence(overview, compareWith))) {
      setCompareWith(ALL_LICENCES);
    }
  }, [compareWith, scope, overview]);

  const comparison = useMemo<DistributionComparison | null>(() => {
    if (!licence) return null;
    const other = scopeLicence(overview, compareWith);
    const workloads = scopeWorkloads(overview, compareWith);
    if (workloads.length === 0) return null;
    return { label: other ? licenceName(other) : everyoneName, workloads };
  }, [licence, overview, compareWith, everyoneName]);

  const ranks = useMemo(() => {
    if (!licence) return undefined;
    const result: Record<string, Rank | null> = {};
    for (const w of WORKLOADS) result[w.key] = workloadRank(overview.licences, licence.licenceTypeId, w.key);
    return result;
  }, [licence, overview.licences]);

  const score = scopeScore(overview, scope);
  const everyoneScore = overview.allLicences?.adoptionScore ?? null;
  const rank = licence ? scoreRank(overview.licences, licence.licenceTypeId) : null;
  const workloads = scopeWorkloads(overview, scope);

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <div className={styles.pickers}>
          <LicenceScopePicker
            licences={overview.licences}
            allAssignedUsers={scopeAssignedUsers(overview, ALL_LICENCES)}
            value={scope}
            onChange={onScopeChange}
            label={t('licenceActivity.common.licence')}
          />
          {licence && (
            <LicenceScopePicker
              licences={overview.licences}
              allAssignedUsers={scopeAssignedUsers(overview, ALL_LICENCES)}
              value={compareWith}
              onChange={setCompareWith}
              label={t('licenceActivity.compare.compareWith')}
              exclude={scope}
            />
          )}
        </div>
        {onShowPeople && (
          <Button appearance="secondary" icon={<People16Regular />} onClick={onShowPeople}>
            {licence ? t('licenceActivity.detail.showPeople') : t('licenceActivity.detail.showChampions')}
          </Button>
        )}
      </div>

      <div className={styles.summary}>
        <div className={styles.figure}>
          <Text size={200} weight="semibold" className={styles.figureLabel}>
            {t('licenceActivity.common.people')}
          </Text>
          <Text size={600} weight="bold">
            {formatCount(scopeAssignedUsers(overview, scope))}
          </Text>
          <Text size={200} className={styles.muted}>
            {licence ? t('licenceActivity.detail.holdThisLicence') : t('licenceActivity.detail.holdAnyLicence')}
          </Text>
        </div>
        <div className={styles.figure}>
          <span className={styles.figureLabel}>
            <Text size={200} weight="semibold">
              {t('licenceActivity.compare.adoptionScore')}
            </Text>
            <AdoptionScoreInfo />
          </span>
          <AdoptionScoreBar score={score} baseline={licence ? everyoneScore : null} large />
          <Text size={200} className={styles.muted}>
            {licence && everyoneScore != null
              ? t('licenceActivity.detail.scoreAgainstEveryone', { score: formatScore(everyoneScore) })
              : t('licenceActivity.detail.scoreOutOf100')}
            {rank ? ` \u00b7 ${t('licenceActivity.detail.scoreRank', { rank: formatCount(rank.rank), of: formatCount(rank.of) })}` : ''}
          </Text>
        </div>
      </div>

      <Text size={300} weight="semibold">
        {t('licenceActivity.detail.servicesFor', { name })}
      </Text>
      <WorkloadDistributions workloads={workloads} comparison={comparison} ranks={ranks} />
    </Card>
  );
}

export default memo(ServiceDetail);
