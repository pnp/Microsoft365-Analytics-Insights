import { useState } from 'react';
import { mergeClasses, tokens, Text, Card, Badge, Link, MessageBar, MessageBarBody } from '@fluentui/react-components';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { formatNumber, plural, useT, type TranslationKey } from '../../i18n';
import {
  formatModelled,
  modelledRange,
  projectLicenceTimeSaved,
  type TimeSavedAssumptionKey,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';
import {
  ACTIVITY_RATIONALE,
  CONSERVATIVE_EVIDENCE,
  SELF_REPORT_CAVEAT,
  TIME_SAVED_BENCHMARKS,
  benchmarkRange,
  senseCheck,
  type SenseCheckVerdict,
} from './coworkTimeSavedEvidence';
import {
  ACTIVITY_ASSUMPTION,
  ACTIVITY_CARD_TITLE,
  ACTIVITY_INPUT_LABEL,
  ACTIVITY_VOLUME_LABEL,
  AssumptionInput,
  CalculatorControls,
  EVIDENCE_GREEN,
  EvidenceEntry,
  MethodBadge,
  ScenarioPicker,
  TIME_SAVED_ACTIVITY_COLOUR,
  TIME_SAVED_ACTIVITY_LABEL,
  formatAssumption,
  useCalculatorFocus,
  useModelStyles,
} from './timeSavedShared';

type Scenario = 'recommended' | 'chatUsers';

function senseCheckKey(verdict: SenseCheckVerdict): TranslationKey {
  switch (verdict) {
    case 'above':
      return 'copilotAdoptionTimeSaved.senseCheck.verdict.above';
    case 'below':
      return 'copilotAdoptionTimeSaved.senseCheck.verdict.below';
    default:
      return 'copilotAdoptionTimeSaved.senseCheck.verdict.within';
  }
}

/**
 * The licence estimate laid open: what was observed, what is assumed, why, and how the result
 * compares with what published studies found.
 *
 * Written for the meeting where somebody pushes back on a licence request. Every assumption is an
 * input the reader can change on the spot, every default states how it was derived from Microsoft's
 * own published credits, every piece of evidence says whether it was measured or self-reported, and
 * the result is compared with published per-person figures. The studies measured Microsoft 365
 * Copilot in the hands of newly licensed people - the largest randomised who received a licence - so
 * they are the right yardstick for exactly this estimate.
 */
export default function LicenceTimeSavedModel({
  summary,
  options,
  timeSaved,
  focusRequest = 0,
}: {
  summary: CopilotAdoptionSummary;
  options: CopilotAdoptionOptions;
  timeSaved: TimeSavedAssumptionState;
  /** Incremented by the headline's "Adjust the assumptions" button - see useCalculatorFocus. */
  focusRequest?: number;
}) {
  const styles = useModelStyles();
  const t = useT();
  const { assumptions, defaults, customised, setAssumption, resetAssumption } = timeSaved;

  const recommended = projectLicenceTimeSaved(summary.licenceOpportunityEstimate, assumptions, options);
  const chatUsers = projectLicenceTimeSaved(summary.licenceChatUsersEstimate, assumptions, options);
  const [scenario, setScenario] = useState<Scenario>('recommended');
  const { ref: calculatorRef, highlighted } = useCalculatorFocus(focusRequest);

  if (!recommended) return null;
  const projection = (scenario === 'chatUsers' ? chatUsers : recommended) ?? recommended;

  const conservativePercent = formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 });
  const maxCandidates = formatNumber(options.maxOpportunityCandidates);
  const range = benchmarkRange();
  // Published figures are averages across the people each study licensed, so the average recommended
  // candidate is the like-for-like comparison, at the conservative end.
  const verdict = senseCheck(recommended.minutesPerPersonDayLow);
  const benchmarkScale = Math.max(range.max, recommended.minutesPerPersonDayHigh) * 1.1;
  const commit = (field: TimeSavedAssumptionKey, value: number) => setAssumption(field, value);

  return (
    <div className={styles.stack}>
      {/* ---------- The calculator ---------- */}
      <div ref={calculatorRef} className={mergeClasses(styles.calculator, highlighted && styles.calculatorHighlighted)}>
        <Card>
          <Text weight="semibold" size={400} className={styles.sectionTitle}>
            {t('copilotAdoptionTimeSaved.licence.model.title')}
          </Text>
          <Text size={200} className={styles.note}>
            {t('copilotAdoptionTimeSaved.licence.model.intro')}
          </Text>

          {chatUsers && (
            <ScenarioPicker<Scenario>
              value={scenario}
              onChange={setScenario}
              options={[
                {
                  value: 'recommended',
                  label: t(
                    plural(
                      recommended.cohortUsers,
                      'copilotAdoptionTimeSaved.licence.model.scenario.recommended.one',
                      'copilotAdoptionTimeSaved.licence.model.scenario.recommended.other',
                    ),
                    { users: formatCount(recommended.cohortUsers) },
                  ),
                },
                {
                  value: 'chatUsers',
                  label: t(
                    plural(
                      chatUsers.cohortUsers,
                      'copilotAdoptionTimeSaved.licence.model.scenario.chatUsers.one',
                      'copilotAdoptionTimeSaved.licence.model.scenario.chatUsers.other',
                    ),
                    { users: formatCount(chatUsers.cohortUsers) },
                  ),
                },
              ]}
            />
          )}

          <div className={styles.tableWrap}>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.work')}</th>
                  <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.table.observed')}</th>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.minutesEach')}</th>
                  <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.table.hours')}</th>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.share')}</th>
                </tr>
              </thead>
              <tbody>
                {projection.activities.map((a) => {
                  const field = ACTIVITY_ASSUMPTION[a.activity];
                  return (
                    <tr key={a.activity}>
                      <td className={styles.td}>
                        <span className={styles.activityCell}>
                          <span className={styles.swatch} style={{ backgroundColor: TIME_SAVED_ACTIVITY_COLOUR[a.activity] }} aria-hidden="true" />
                          <span>
                            <Text size={300} weight="semibold">
                              {t(TIME_SAVED_ACTIVITY_LABEL[a.activity])}
                            </Text>
                            <Text size={100} className={styles.sub}>
                              {t(ACTIVITY_VOLUME_LABEL[a.activity])}
                            </Text>
                          </span>
                        </span>
                      </td>
                      <td className={mergeClasses(styles.td, styles.tdNumeric)}>{formatCount(a.volume)}</td>
                      <td className={styles.td}>
                        <AssumptionInput
                          field={field}
                          value={assumptions[field]}
                          defaultValue={defaults[field]}
                          customised={customised.includes(field)}
                          label={t(ACTIVITY_INPUT_LABEL[a.activity])}
                          unit={t('copilotAdoptionTimeSaved.unit.minutes')}
                          onCommit={commit}
                          onReset={resetAssumption}
                        />
                      </td>
                      <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                        {t('copilotAdoptionTimeSaved.table.hoursValue', { hours: formatCount(a.displayHours) })}
                      </td>
                      <td className={styles.td}>
                        <div className={styles.shareBar} title={`${formatNumber(a.sharePct, { maximumFractionDigits: 0 })}%`}>
                          <div
                            style={{
                              width: `${Math.max(0, Math.min(100, a.sharePct))}%`,
                              height: '100%',
                              backgroundColor: TIME_SAVED_ACTIVITY_COLOUR[a.activity],
                            }}
                          />
                        </div>
                      </td>
                    </tr>
                  );
                })}
                <tr className={styles.totalRow}>
                  <td className={styles.td} colSpan={3}>
                    {t('copilotAdoptionTimeSaved.table.total')}
                  </td>
                  <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                    <span className={styles.bigHours}>
                      {t('copilotAdoptionTimeSaved.table.hoursValue', { hours: formatCount(projection.hoursHigh) })}
                    </span>
                  </td>
                  <td className={styles.td} />
                </tr>
                <tr className={styles.totalRow}>
                  <td className={styles.td} colSpan={2}>
                    {t('copilotAdoptionTimeSaved.table.conservativeEnd')}
                  </td>
                  <td className={styles.td}>
                    <AssumptionInput
                      field="conservativeRatio"
                      value={assumptions.conservativeRatio}
                      defaultValue={defaults.conservativeRatio}
                      customised={customised.includes('conservativeRatio')}
                      label={t('copilotAdoptionTimeSaved.input.conservativePercent')}
                      unit="%"
                      scale={100}
                      onCommit={commit}
                      onReset={resetAssumption}
                    />
                  </td>
                  <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                    <span className={styles.bigHours}>
                      {t('copilotAdoptionTimeSaved.table.hoursValue', { hours: formatCount(projection.hoursLow) })}
                    </span>
                  </td>
                  <td className={styles.td} />
                </tr>
              </tbody>
            </table>
          </div>

          <CalculatorControls timeSaved={timeSaved} />
        </Card>
      </div>

      {/* ---------- Why each figure ---------- */}
      <div>
        <Text weight="semibold" size={400} className={styles.sectionTitle}>
          {t('copilotAdoptionTimeSaved.rationale.title')}
        </Text>
        <Text size={200} className={styles.note}>
          {t('copilotAdoptionTimeSaved.rationale.intro')}
        </Text>
      </div>

      <div className={styles.grid}>
        {ACTIVITY_RATIONALE.map((r) => {
          const field = ACTIVITY_ASSUMPTION[r.activity];
          const defaultMinutes = formatAssumption(defaults[field]);
          return (
            <Card key={r.activity} className={styles.card} style={{ borderTopColor: TIME_SAVED_ACTIVITY_COLOUR[r.activity] }}>
              <div className={styles.cardHead}>
                <Text weight="semibold" size={400}>
                  {t(ACTIVITY_CARD_TITLE[r.activity], { minutes: defaultMinutes })}
                </Text>
                {customised.includes(field) && (
                  <Badge size="small" appearance="tint" color="brand">
                    {t('copilotAdoptionTimeSaved.input.usingYours', { minutes: formatAssumption(assumptions[field]) })}
                  </Badge>
                )}
              </div>
              <div>
                <Text size={100} weight="semibold" className={styles.label}>
                  {t('copilotAdoptionTimeSaved.rationale.whatSavesTime')}
                </Text>
                <ul className={styles.list}>
                  {r.operationKeys.map((key) => (
                    <li key={key}>
                      <Text size={200}>{t(key)}</Text>
                    </li>
                  ))}
                </ul>
              </div>
              <div>
                <Text size={100} weight="semibold" className={styles.label}>
                  {t('copilotAdoptionTimeSaved.rationale.whyThisFigure', { minutes: defaultMinutes })}
                </Text>
                <Text size={200}>{t(r.whyKey)}</Text>
              </div>
              <div>
                <Text size={100} weight="semibold" className={styles.label}>
                  {t('copilotAdoptionTimeSaved.rationale.evidence')}
                </Text>
                <ul className={styles.evidenceList}>
                  {r.evidence.map((item) => (
                    <EvidenceEntry key={item.id} item={item} />
                  ))}
                </ul>
              </div>
              <div className={styles.test}>
                <Text size={100} weight="semibold" className={styles.label}>
                  {t('copilotAdoptionTimeSaved.rationale.testIt')}
                </Text>
                <Text size={200}>{t(r.testKey)}</Text>
              </div>
            </Card>
          );
        })}
      </div>

      <div className={styles.grid}>
        {/* ---------- The conservative end ---------- */}
        <Card className={styles.card} style={{ borderTopColor: tokens.colorNeutralStroke1 }}>
          <Text weight="semibold" size={400}>
            {t('copilotAdoptionTimeSaved.conservative.title', { percent: conservativePercent })}
          </Text>
          <Text size={200}>{t('copilotAdoptionTimeSaved.conservative.why')}</Text>
          <ul className={styles.evidenceList}>
            {CONSERVATIVE_EVIDENCE.map((item) => (
              <EvidenceEntry key={item.id} item={item} />
            ))}
          </ul>
        </Card>

        {/* ---------- The sense check ---------- */}
        <Card className={styles.card} style={{ borderTopColor: tokens.colorBrandStroke1 }}>
          <Text weight="semibold" size={400}>
            {t('copilotAdoptionTimeSaved.senseCheck.title')}
          </Text>
          <Text size={200}>
            {t('copilotAdoptionTimeSaved.senseCheck.intro', {
              range: modelledRange(
                t,
                formatModelled(recommended.minutesPerPersonDayLow),
                formatModelled(recommended.minutesPerPersonDayHigh),
              ),
            })}
          </Text>

          <div role="list" aria-label={t('copilotAdoptionTimeSaved.senseCheck.title')}>
            <div role="listitem" className={mergeClasses(styles.benchmarkRow, styles.modelRow)}>
              <Text size={200} weight="semibold">
                {t('copilotAdoptionTimeSaved.senseCheck.yourModelAverage')}
              </Text>
              <div className={styles.benchmarkBarTrack} aria-hidden="true">
                <div
                  className={styles.benchmarkBar}
                  style={{
                    width: `${Math.min(100, (recommended.minutesPerPersonDayHigh / benchmarkScale) * 100)}%`,
                    backgroundColor: tokens.colorBrandBackground,
                    opacity: 0.35,
                  }}
                />
                <div
                  className={styles.benchmarkBar}
                  style={{
                    width: `${Math.min(100, (recommended.minutesPerPersonDayLow / benchmarkScale) * 100)}%`,
                    backgroundColor: tokens.colorBrandBackground,
                  }}
                />
              </div>
              <Text size={200} weight="semibold" style={{ whiteSpace: 'nowrap' }}>
                {t('copilotAdoptionTimeSaved.senseCheck.minutesRange', {
                  range: modelledRange(
                    t,
                    formatModelled(recommended.minutesPerPersonDayLow),
                    formatModelled(recommended.minutesPerPersonDayHigh),
                  ),
                })}
              </Text>
            </div>
            {TIME_SAVED_BENCHMARKS.map((b) => (
              <div key={b.id} role="listitem" className={styles.benchmarkRow}>
                <span className={styles.evidenceItem}>
                  <span className={styles.evidenceHead}>
                    <MethodBadge method={b.method} />
                  </span>
                  <Link href={b.url} target="_blank" rel="noopener noreferrer">
                    <Text size={200}>{t(b.sourceKey)}</Text>
                  </Link>
                  <Text size={100} className={styles.sub}>
                    {t(b.findingKey)}
                  </Text>
                </span>
                <div className={styles.benchmarkBarTrack} aria-hidden="true">
                  <div
                    className={styles.benchmarkBar}
                    style={{
                      width: `${Math.min(100, (b.minutesPerDay / benchmarkScale) * 100)}%`,
                      backgroundColor: b.method === 'measured' ? EVIDENCE_GREEN : tokens.colorNeutralForeground3,
                    }}
                  />
                </div>
                <Text size={200} style={{ whiteSpace: 'nowrap' }}>
                  {t('copilotAdoptionTimeSaved.senseCheck.minutes', { minutes: formatNumber(b.minutesPerDay) })}
                </Text>
              </div>
            ))}
          </div>

          <MessageBar intent={verdict === 'above' ? 'warning' : 'success'}>
            <MessageBarBody>
              {t(senseCheckKey(verdict), {
                min: formatNumber(range.min),
                max: formatNumber(range.max),
              })}
            </MessageBarBody>
          </MessageBar>
          <Text size={100} className={styles.sub}>
            {t(SELF_REPORT_CAVEAT.findingKey)}{' '}
            <Link href={SELF_REPORT_CAVEAT.url} target="_blank" rel="noopener noreferrer">
              {t(SELF_REPORT_CAVEAT.sourceKey)}
            </Link>
          </Text>
        </Card>
      </div>

      {/* ---------- Before you quote it ---------- */}
      <div className={styles.grid}>
        <Card>
          <Text weight="semibold" size={400}>
            {t('copilotAdoptionTimeSaved.caveats.title')}
          </Text>
          <ul className={styles.assumptionList}>
            <li key="licence-assumption-saves">
              <Text size={200}>
                {t('copilotAdoptionTimeSaved.licence.assumption.saves', {
                  meetingMinutes: formatNumber(assumptions.meetingMinutes, { maximumFractionDigits: 15 }),
                  emailMinutes: formatNumber(assumptions.emailMinutes, { maximumFractionDigits: 15 }),
                  documentMinutes: formatNumber(assumptions.documentMinutes, { maximumFractionDigits: 15 }),
                })}
              </Text>
            </li>
            <li key="licence-assumption-volumes">
              <Text size={200}>
                {t(
                  plural(
                    projection.cohortUsers,
                    'copilotAdoptionTimeSaved.licence.assumption.volumes.one',
                    'copilotAdoptionTimeSaved.licence.assumption.volumes.other',
                  ),
                  {
                    users: formatNumber(projection.cohortUsers),
                    workingDays: formatNumber(projection.workingDaysPerMonth, { maximumFractionDigits: 15 }),
                  },
                )}
              </Text>
            </li>
            <li key="licence-assumption-chatUsers">
              <Text size={200}>{t('copilotAdoptionTimeSaved.licence.assumption.chatUsers')}</Text>
            </li>
            {projection.candidatesCapped && (
              <li key="licence-assumption-capped">
                <Text size={200}>
                  {t('copilotAdoptionTimeSaved.licence.assumption.capped', { cap: maxCandidates })}
                </Text>
              </li>
            )}
            <li key="licence-assumption-lowerBound">
              <Text size={200}>
                {t('copilotAdoptionTimeSaved.licence.assumption.lowerBound', { percent: conservativePercent })}
              </Text>
            </li>
            <li key="licence-assumption-potential">
              <Text size={200}>{t('copilotAdoptionTimeSaved.licence.assumption.potential')}</Text>
            </li>
            <li key="licence-assumption-notMeasured">
              <Text size={200}>{t('copilotAdoptionTimeSaved.licence.assumption.notMeasured')}</Text>
            </li>
            <li key="licence-assumption-noMoney">
              <Text size={200}>{t('copilotAdoptionTimeSaved.licence.assumption.noMoney')}</Text>
            </li>
          </ul>
        </Card>

        <Card>
          <Text weight="semibold" size={400}>
            {t('copilotAdoptionTimeSaved.own.title')}
          </Text>
          <ol className={styles.steps}>
            <li>
              <Text size={200}>{t('copilotAdoptionTimeSaved.own.pilot')}</Text>
            </li>
            <li>
              <Text size={200}>{t('copilotAdoptionTimeSaved.own.askPerItem')}</Text>
            </li>
            <li>
              <Text size={200}>{t('copilotAdoptionTimeSaved.own.compareDashboard')}</Text>
            </li>
            <li>
              <Text size={200}>{t('copilotAdoptionTimeSaved.own.enter')}</Text>
            </li>
          </ol>
        </Card>
      </div>
    </div>
  );
}
