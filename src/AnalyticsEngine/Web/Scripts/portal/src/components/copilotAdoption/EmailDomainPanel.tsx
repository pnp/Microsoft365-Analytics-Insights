import { makeStyles, tokens, Text, Badge, Button } from '@fluentui/react-components';
import type { AdoptionDomainRow, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount, formatPct } from '../shared/KpiGrid';
import InfoTip from '../shared/InfoTip';
import { rateColour, scoreColour, ScoreBar, useAdoptionTableStyles } from './adoptionShared';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import { useT } from '../../i18n';

const useStyles = makeStyles({
  intro: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    marginBottom: '12px',
    maxWidth: '78ch',
    lineHeight: tokens.lineHeightBase300,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    padding: '12px 0',
    maxWidth: '78ch',
    lineHeight: tokens.lineHeightBase300,
  },
  domainCell: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
  },
  filterButton: {
    minWidth: 'auto',
    paddingLeft: '6px',
    paddingRight: '6px',
  },
  noSeats: {
    color: tokens.colorNeutralForeground3,
  },
  selected: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
  },
});

/** Domains with no seats have no adoption rate; a dash says so rather than implying 0%. */
const NO_VALUE = '\u2014';

/**
 * Adoption compared across the email domains sharing this tenant.
 *
 * A tenant assembled from acquisitions carries several verified domains, and they adopt Copilot
 * very differently - the company that ran the rollout is not the one whose seats were handed out
 * during a migration. Department cuts across all of them and averages exactly that difference away,
 * which is why this is its own view rather than another entry in the department chart.
 *
 * Four populations per row, because per organisation they answer one question rather than four:
 * idle seats next to unlicensed Copilot Chat use is a seat-allocation problem that can usually be
 * fixed at no cost, while strong adoption next to a queue of licence candidates is a business case.
 */
export default function EmailDomainPanel({
  summary,
  selectedDomain,
  onSelectDomain,
}: {
  summary: CopilotAdoptionSummary;
  /** The domain the whole page is currently narrowed to, if any. */
  selectedDomain?: string | null;
  /**
   * Narrows every visual on the page to one domain. Omitted when the panel is rendered somewhere
   * the page-wide filter does not apply, in which case no filter buttons are shown.
   */
  onSelectDomain?: (domain: string | null) => void;
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();
  const t = useT();
  const rows: AdoptionDomainRow[] = summary.emailDomains ?? [];
  const o = summary.options;
  // The same tuned thresholds the rest of the page colours by, so one score is not two colours.
  const bands = {
    champion: o.championScore,
    established: o.establishedScore,
    developing: o.developingScore,
  };
  const showCowork = summary.coworkReadinessAvailable && rows.some((r) => r.coworkPrimeCandidates > 0);

  if (rows.length === 0) {
    return (
      <div className={styles.empty}>
        {t('copilotAdoptionUsers.emailDomain.emptyNotEnough', { count: o.minSeatsPerSegment })}
      </div>
    );
  }

  if (rows.length === 1) {
    return (
      <div className={styles.empty}>
        {t('copilotAdoptionUsers.emailDomain.singleDomain', { domain: serverPlaceholderText(t, rows[0].segment) })}
      </div>
    );
  }

  return (
    <div>
      <div className={styles.intro}>{t('copilotAdoptionUsers.emailDomain.intro')}</div>

      <table className={table.table}>
        <thead>
          <tr>
            <th className={table.th}>{t('copilotAdoptionUsers.emailDomain.emailDomainHeader')}</th>
            <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionUsers.emailDomain.licencesHeader')}</th>
            <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionUsers.emailDomain.activeHeader')}</th>
            <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionUsers.emailDomain.habitualHeader')}</th>
            <th className={table.th}>{t('copilotAdoptionUsers.emailDomain.adoptionRateHeader')}</th>
            <th className={table.th}>{t('copilotAdoptionUsers.emailDomain.avgScoreHeader')}</th>
            <th className={`${table.th} ${table.thNumeric}`}>
              {t('copilotAdoptionUsers.emailDomain.reclaimableHeader')}
              <InfoTip
                title={t('copilotAdoptionUsers.emailDomain.reclaimableTitle')}
                content={{
                  what: t('copilotAdoptionUsers.emailDomain.reclaimableWhat'),
                  how: t('copilotAdoptionUsers.emailDomain.reclaimableHow'),
                  source: t('copilotAdoptionUsers.emailDomain.reclaimableSource'),
                }}
              />
            </th>
            <th className={`${table.th} ${table.thNumeric}`}>
              {t('copilotAdoptionUsers.emailDomain.interactionsPerLicenceHeader')}
              <InfoTip
                title={t('copilotAdoptionUsers.emailDomain.interactionsPerLicenceTitle')}
                content={{
                  what: t('copilotAdoptionUsers.emailDomain.interactionsPerLicenceWhat'),
                  how: t('copilotAdoptionUsers.emailDomain.interactionsPerLicenceHow'),
                  formula: t('copilotAdoptionUsers.emailDomain.interactionsPerLicenceFormula'),
                }}
              />
            </th>
            <th className={`${table.th} ${table.thNumeric}`}>
              {t('copilotAdoptionUsers.emailDomain.unlicensedUsersHeader')}
              <InfoTip
                title={t('copilotAdoptionUsers.emailDomain.unlicensedUsersTitle')}
                content={{
                  what: t('copilotAdoptionUsers.emailDomain.unlicensedUsersWhat'),
                  how: t('copilotAdoptionUsers.emailDomain.unlicensedUsersHow'),
                  source: t('copilotAdoptionUsers.emailDomain.unlicensedUsersSource'),
                }}
              />
            </th>
            <th className={`${table.th} ${table.thNumeric}`}>
              {t('copilotAdoptionUsers.emailDomain.licenceCandidatesHeader')}
              <InfoTip
                title={t('copilotAdoptionUsers.emailDomain.licenceCandidatesTitle')}
                content={{
                  what: t('copilotAdoptionUsers.emailDomain.licenceCandidatesWhat'),
                  how: t('copilotAdoptionUsers.emailDomain.licenceCandidatesHow'),
                  source: t('copilotAdoptionUsers.emailDomain.licenceCandidatesSource'),
                }}
              />
            </th>
            {showCowork && <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionUsers.emailDomain.coworkCandidatesHeader')}</th>}
            {onSelectDomain && <th className={table.th} aria-label={t('copilotAdoptionUsers.emailDomain.filterAria')} data-print="hide" />}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => {
            const isSelected = !!selectedDomain && selectedDomain === row.segment;
            const hasSeats = row.licensedUsers > 0;

            return (
              <tr key={row.segment} className={isSelected ? styles.selected : undefined}>
                <td className={table.td}>
                  <span className={styles.domainCell}>
                    <span>{serverPlaceholderText(t, row.segment)}</span>
                    {row.external && (
                      <Badge appearance="outline" color="informative" size="small">
                        {t('copilotAdoptionUsers.emailDomain.external')}
                      </Badge>
                    )}
                  </span>
                </td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.licensedUsers)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.activeUsers)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.habitualUsers)}</td>
                <td className={table.td}>
                  {hasSeats ? (
                    <div className={table.scoreCell}>
                      <div className={table.scoreTrack}>
                        <div
                          className={table.scoreBar}
                          style={{
                            width: `${Math.max(0, Math.min(100, row.adoptionRatePct))}%`,
                            backgroundColor: rateColour(row.adoptionRatePct),
                          }}
                        />
                      </div>
                      <Text size={200} weight="semibold" className={table.scoreValue}>
                        {formatPct(row.adoptionRatePct)}
                      </Text>
                    </div>
                  ) : (
                    // No seats means there is no adoption rate to report. A 0% here would read as
                    // "this organisation ignores Copilot" when the truth is it was never offered any.
                    <Text size={200} className={styles.noSeats} title={t('copilotAdoptionUsers.emailDomain.noSeatsTitle')}>
                      {NO_VALUE} {t('copilotAdoptionUsers.emailDomain.noSeats')}
                    </Text>
                  )}
                </td>
                <td className={table.td}>
                  {hasSeats ? (
                    <ScoreBar score={row.averageAdoptionScore} colour={scoreColour(row.averageAdoptionScore, bands)} />
                  ) : (
                    <Text size={200} className={styles.noSeats}>
                      {NO_VALUE}
                    </Text>
                  )}
                </td>
                <td className={`${table.td} ${table.tdNumeric}`}>
                  {hasSeats ? formatCount(row.reclaimableSeats) : NO_VALUE}
                </td>
                <td className={`${table.td} ${table.tdNumeric}`}>
                  {hasSeats ? row.interactionsPerLicensedUser : NO_VALUE}
                </td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.unlicensedActiveUsers)}</td>
                <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.recommendedForLicence)}</td>
                {showCowork && (
                  <td className={`${table.td} ${table.tdNumeric}`}>
                    {hasSeats ? formatCount(row.coworkPrimeCandidates) : NO_VALUE}
                  </td>
                )}
                {onSelectDomain && (
                  <td className={table.td} data-print="hide">
                    <Button
                      size="small"
                      appearance={isSelected ? 'primary' : 'subtle'}
                      className={styles.filterButton}
                      onClick={() => onSelectDomain(isSelected ? null : row.segment)}
                    >
                      {isSelected ? t('copilotAdoptionUsers.emailDomain.clear') : t('copilotAdoptionUsers.emailDomain.filter')}
                    </Button>
                  </td>
                )}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
