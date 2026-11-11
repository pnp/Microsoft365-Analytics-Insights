import { makeStyles, tokens, Text, Badge, Button } from '@fluentui/react-components';
import type { AdoptionDomainRow, CopilotAdoptionSummary, SmallGroupWithheldReason } from '../../types/copilotAdoption';
import { formatCount, formatPct } from '../shared/KpiGrid';
import InfoTip from '../shared/InfoTip';
import { rateColour, scoreColour, ScoreBar, useAdoptionTableStyles } from './adoptionShared';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import { formatNumber, useT, type TFunction, type TranslationKey } from '../../i18n';

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
  withheld: {
    color: tokens.colorNeutralForeground3,
  },
  note: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    marginTop: '8px',
    maxWidth: '78ch',
    lineHeight: tokens.lineHeightBase300,
  },
});

/** Domains with no seats have no adoption rate; a dash says so rather than implying 0%. */
const NO_VALUE = '\u2014';

/** The server's floor for a reader without See PII (`ReportScopeResolver.MinimumPeopleWithoutSeePii`), when a row does not carry it. */
const WITHHELD_FLOOR = 5;

/** The three populations a row holds, each floored on its own for a reader without See PII (#844). */
type DomainPopulation = 'seats' | 'unlicensed' | 'candidates';

const WITHHELD_KEYS: Record<DomainPopulation, Record<SmallGroupWithheldReason, TranslationKey>> = {
  seats: {
    fewPeople: 'copilotAdoptionUsers.emailDomain.withheld.seats.fewPeople',
    revealsSmallerGroup: 'copilotAdoptionUsers.emailDomain.withheld.seats.revealsSmallerGroup',
  },
  unlicensed: {
    fewPeople: 'copilotAdoptionUsers.emailDomain.withheld.unlicensed.fewPeople',
    revealsSmallerGroup: 'copilotAdoptionUsers.emailDomain.withheld.unlicensed.revealsSmallerGroup',
  },
  candidates: {
    fewPeople: 'copilotAdoptionUsers.emailDomain.withheld.candidates.fewPeople',
    revealsSmallerGroup: 'copilotAdoptionUsers.emailDomain.withheld.candidates.revealsSmallerGroup',
  },
};

/** Why a population's figures are not shown; a reason this build does not know gets the general sentence. */
function withheldText(t: TFunction, row: AdoptionDomainRow, population: DomainPopulation, reason: string): string {
  const key = WITHHELD_KEYS[population][reason as SmallGroupWithheldReason] ?? 'copilotAdoptionUsers.emailDomain.withheld.other';
  return t(key, { count: formatCount(row.minimumPeople ?? WITHHELD_FLOOR) });
}

function withheldReason(row: AdoptionDomainRow, population: DomainPopulation): string | null {
  switch (population) {
    case 'seats':
      return row.seatHoldersWithheld ?? (row.licensedUsers == null ? 'unknown' : null);
    case 'unlicensed':
      return row.unlicensedUsersWithheld ?? (row.unlicensedActiveUsers == null ? 'unknown' : null);
    case 'candidates':
      return row.licenceCandidatesWithheld ?? (row.recommendedForLicence == null ? 'unknown' : null);
  }
}

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
  const showCowork = summary.coworkReadinessAvailable && rows.some((r) => (r.coworkPrimeCandidates ?? 0) > 0);
  const anyWithheld = rows.some((r) =>
    (['seats', 'unlicensed', 'candidates'] as DomainPopulation[]).some((p) => withheldReason(r, p) !== null));
  const complement = rows.some((r) =>
    r.seatHoldersWithheld === 'revealsSmallerGroup'
    || r.unlicensedUsersWithheld === 'revealsSmallerGroup'
    || r.licenceCandidatesWithheld === 'revealsSmallerGroup');
  const floor = rows.find((r) => r.minimumPeople != null)?.minimumPeople ?? WITHHELD_FLOOR;

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
            // Withheld seat holders are not "no seats": the row says why their figures are hidden instead (#844).
            const seatsWithheld = withheldReason(row, 'seats');
            const hasSeats = seatsWithheld === null && (row.licensedUsers ?? 0) > 0;
            const unlicensedWithheld = withheldReason(row, 'unlicensed');
            const candidatesWithheld = withheldReason(row, 'candidates');
            const dash = (population: DomainPopulation, reason: string) => {
              const text = withheldText(t, row, population, reason);
              return (
                <span className={styles.withheld} title={text} aria-label={text}>
                  {NO_VALUE}
                </span>
              );
            };

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
                {seatsWithheld !== null ? (
                  // One sentence across every seat-holder column, rather than seven dashes that each need explaining.
                  <td className={table.td} colSpan={7}>
                    <Text size={200} className={styles.withheld}>
                      {withheldText(t, row, 'seats', seatsWithheld)}
                    </Text>
                  </td>
                ) : (
                  <>
                    <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.licensedUsers ?? 0)}</td>
                    <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.activeUsers ?? 0)}</td>
                    <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.habitualUsers ?? 0)}</td>
                    <td className={table.td}>
                      {hasSeats ? (
                        <div className={table.scoreCell}>
                          <div className={table.scoreTrack}>
                            <div
                              className={table.scoreBar}
                              style={{
                                width: `${Math.max(0, Math.min(100, row.adoptionRatePct ?? 0))}%`,
                                backgroundColor: rateColour(row.adoptionRatePct ?? 0),
                              }}
                            />
                          </div>
                          <Text size={200} weight="semibold" className={table.scoreValue}>
                            {formatPct(row.adoptionRatePct ?? 0)}
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
                        <ScoreBar score={row.averageAdoptionScore ?? 0} colour={scoreColour(row.averageAdoptionScore ?? 0, bands)} />
                      ) : (
                        <Text size={200} className={styles.noSeats}>
                          {NO_VALUE}
                        </Text>
                      )}
                    </td>
                    <td className={`${table.td} ${table.tdNumeric}`}>
                      {hasSeats ? formatCount(row.reclaimableSeats ?? 0) : NO_VALUE}
                    </td>
                    <td className={`${table.td} ${table.tdNumeric}`}>
                      {hasSeats ? formatNumber(row.interactionsPerLicensedUser ?? 0, { maximumFractionDigits: 1 }) : NO_VALUE}
                    </td>
                  </>
                )}
                <td className={`${table.td} ${table.tdNumeric}`}>
                  {unlicensedWithheld !== null ? dash('unlicensed', unlicensedWithheld) : formatCount(row.unlicensedActiveUsers ?? 0)}
                </td>
                <td className={`${table.td} ${table.tdNumeric}`}>
                  {candidatesWithheld !== null ? dash('candidates', candidatesWithheld) : formatCount(row.recommendedForLicence ?? 0)}
                </td>
                {showCowork && (
                  <td className={`${table.td} ${table.tdNumeric}`}>
                    {seatsWithheld !== null
                      ? dash('seats', seatsWithheld)
                      : hasSeats ? formatCount(row.coworkPrimeCandidates ?? 0) : NO_VALUE}
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
      {anyWithheld && (
        <div className={styles.note}>
          {t('copilotAdoptionUsers.emailDomain.withheldNote', { count: formatCount(floor) })}
          {complement && (
            <>
              {' '}
              {t('copilotAdoptionUsers.emailDomain.withheldNoteComplement')}
            </>
          )}
        </div>
      )}
    </div>
  );
}
