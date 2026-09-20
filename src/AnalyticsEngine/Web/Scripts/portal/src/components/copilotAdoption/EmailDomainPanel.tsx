import { makeStyles, tokens, Text, Badge, Button } from '@fluentui/react-components';
import type { AdoptionDomainRow, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount, formatPct } from '../shared/KpiGrid';
import InfoTip from '../shared/InfoTip';
import { rateColour, scoreColour, ScoreBar, useAdoptionTableStyles } from './adoptionShared';

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
        No email domain has enough people to compare reliably. Domains need at least{' '}
        {o.minSeatsPerSegment} licensed or unlicensed Copilot users to appear, so a tenant with a single
        verified domain legitimately shows nothing here.
      </div>
    );
  }

  if (rows.length === 1) {
    return (
      <div className={styles.empty}>
        Everybody in this analysis is on one email domain ({rows[0].segment}), so there is nothing to
        compare. This view is for tenants that carry several domains - typically acquisitions that were
        never rebranded, subsidiaries, or a separate contractor domain.
      </div>
    );
  }

  return (
    <div>
      <div className={styles.intro}>
        Each row is one of the organisations sharing this tenant, identified by the domain in its users&apos;
        sign-in names. Worst adoption first. Domains with no seats at all are listed last because they have
        no adoption rate to rank on - those are the businesses using Copilot Chat without ever having been
        given a licence, and they are usually the most actionable rows here. Guests are counted under their
        own home domain, not this tenant&apos;s, and are flagged External.
      </div>

      <table className={table.table}>
        <thead>
          <tr>
            <th className={table.th}>Email domain</th>
            <th className={`${table.th} ${table.thNumeric}`}>Licences</th>
            <th className={`${table.th} ${table.thNumeric}`}>Active</th>
            <th className={`${table.th} ${table.thNumeric}`}>Habitual</th>
            <th className={table.th}>Adoption rate</th>
            <th className={table.th}>Avg. score</th>
            <th className={`${table.th} ${table.thNumeric}`}>
              Reclaimable
              <InfoTip
                title="Reclaimable seats"
                content={{
                  what: 'Copilot seats on this domain that look reclaimable.',
                  how: 'The same certain/probable confidence tiering the headline reclaim figure uses, applied to this domain\u2019s seat holders only.',
                  source: 'Copilot audit import and Microsoft\u2019s Copilot usage report, plus the account-enabled flag from the user metadata import.',
                }}
              />
            </th>
            <th className={`${table.th} ${table.thNumeric}`}>
              Interactions per licence
              <InfoTip
                title="Interactions per licence"
                content={{
                  what: 'How much Copilot each seat on this domain is getting used for, per month.',
                  how: 'Divides by EVERY licence including idle ones - that is the point of comparing it with the unlicensed column next to it. Rows scored from Microsoft\u2019s usage report carry prompt counts rather than audit interactions, so they stay in the denominator but never the numerator.',
                  formula: 'audit interactions / licences held, normalised to a month',
                }}
              />
            </th>
            <th className={`${table.th} ${table.thNumeric}`}>
              Unlicensed users
              <InfoTip
                title="Unlicensed Copilot users"
                content={{
                  what: 'People on this domain who used Copilot Chat in the period without holding a seat.',
                  how: 'A domain with idle licences AND heavy unlicensed use is a seat-allocation problem rather than an adoption problem, and can usually be fixed at no cost by moving seats between the two groups.',
                  source: 'Copilot audit import. Guests are excluded from this population.',
                }}
              />
            </th>
            <th className={`${table.th} ${table.thNumeric}`}>
              Licence candidates
              <InfoTip
                title="Licence candidates"
                content={{
                  what: 'People on this domain the licence-opportunity ranking recommends buying a Copilot seat for.',
                  how: 'Ranked on existing unlicensed Copilot use first, then on general Microsoft 365 workload. Only those scoring above the recommendation bar are counted here.',
                  source: 'The Licence opportunities tab lists them by name, and its CSV export is the same population.',
                }}
              />
            </th>
            {showCowork && <th className={`${table.th} ${table.thNumeric}`}>Cowork candidates</th>}
            {onSelectDomain && <th className={table.th} aria-label="Filter" />}
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
                    <span>{row.segment}</span>
                    {row.external && (
                      <Badge appearance="outline" color="informative" size="small">
                        External
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
                    <Text size={200} className={styles.noSeats} title="No Copilot seats on this domain">
                      {NO_VALUE} no seats
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
                  <td className={table.td}>
                    <Button
                      size="small"
                      appearance={isSelected ? 'primary' : 'subtle'}
                      className={styles.filterButton}
                      onClick={() => onSelectDomain(isSelected ? null : row.segment)}
                    >
                      {isSelected ? 'Clear' : 'Filter'}
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
