import { useState } from 'react';
import { mergeClasses, Text, Card, Badge, Link, MessageBar, MessageBarBody } from '@fluentui/react-components';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { formatNumber, plural, useT, useTNode } from '../../i18n';
import {
  COWORK_ACTIVITIES,
  COWORK_ACTIVITY_ASSUMPTIONS,
  customisesAny,
  formatModelled,
  projectCoworkTimeSaved,
  type TimeSavedAssumptionKey,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';
import { COWORK_ACTIVITY_SHARE_WHY, COWORK_OVERVIEW_URL, COWORK_TASK_RATIONALE } from './coworkTimeSavedEvidence';
import {
  AssumptionBadge,
  AssumptionInput,
  CalculatorControls,
  COWORK_ACTIVITY_COLOUR,
  COWORK_ACTIVITY_LABEL,
  COWORK_ACTIVITY_MINUTES_INPUT_LABEL,
  COWORK_ACTIVITY_SHARE_INPUT_LABEL,
  COWORK_ACTIVITY_VOLUME_LABEL,
  COWORK_OBSERVED_COLOUR,
  EvidenceEntry,
  ScenarioPicker,
  TIME_SAVED_COWORK_COLOUR,
  formatAssumption,
  useCalculatorFocus,
  useModelStyles,
} from './timeSavedShared';

type Scenario = 'ready' | 'full';

/** A share as the figure a sentence quotes before its percent sign: 0.25 -> "25". */
function percentFigure(share: number): string {
  return formatNumber(share * 100, { maximumFractionDigits: 2 });
}

/** Every minutes figure the Cowork estimate uses - the task minutes and each kind of work's. */
const COWORK_MINUTES_KEYS: readonly TimeSavedAssumptionKey[] = [
  'taskMinutes',
  ...COWORK_ACTIVITIES.map((activity) => COWORK_ACTIVITY_ASSUMPTIONS[activity].minutes),
];

/**
 * The Cowork estimate laid open: where the time would come from, what was observed, what is assumed,
 * and what is and is not known.
 *
 * Written for the meeting where somebody pushes back on the number. The people not yet running Cowork
 * are modelled from the work they already do by hand - one row per kind of work Cowork can take on,
 * each with the share of it handed over and the minutes saved on each piece as inputs the reader can
 * change on the spot - and the page says plainly that no study has measured either. So the figure is
 * sized honestly for what it is: the value of enabling Cowork for people who already hold a Copilot
 * licence, on top of what that licence gives back. The only thing it can be checked against is the
 * tenant's own Cowork users, and the shares card does that; the Copilot evidence and its sense check
 * sit with the licence estimate, on the Licence opportunities tab.
 */
export default function CoworkTimeSavedModel({
  summary,
  options,
  timeSaved,
  focusRequest = 0,
}: {
  summary: CopilotAdoptionSummary;
  options: CopilotAdoptionOptions;
  timeSaved: TimeSavedAssumptionState;
  /**
   * Incremented by the headline's "Adjust the assumptions" button. Each new value scrolls the
   * calculator into view, focuses its first figure and briefly rings it.
   */
  focusRequest?: number;
}) {
  const styles = useModelStyles();
  const t = useT();
  const tNode = useTNode();
  const { assumptions, defaults, customised, setAssumption, resetAssumption } = timeSaved;

  const ready = projectCoworkTimeSaved(summary.coworkValueEstimate, assumptions, options);
  const full = projectCoworkTimeSaved(summary.coworkFullRolloutEstimate, assumptions, options);
  // The people ready now first, as on the headline: that is the spending-policy decision.
  const [scenario, setScenario] = useState<Scenario>(ready ? 'ready' : 'full');
  const { ref: calculatorRef, highlighted } = useCalculatorFocus(focusRequest);

  const projection = (scenario === 'ready' ? ready : full) ?? ready ?? full;

  if (!projection) {
    // Without the usage reports there is no work to model, which is a missing import, not a finding
    // that Cowork has nothing to take on - so it is said as one.
    const noActivity = summary.coworkReadinessAvailable && summary.dataSources?.m365UsageReportsAvailable === false;
    return (
      <Card>
        <Text weight="semibold" block>
          {t('copilotAdoptionCowork.timeSaved.model.title')}
        </Text>
        <Text size={200} className={styles.note}>
          {t(noActivity ? 'copilotAdoptionCowork.timeSaved.model.noActivity' : 'copilotAdoptionCowork.timeSaved.model.empty')}
        </Text>
      </Card>
    );
  }

  const conservativePercent = formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 });
  const monthDays = Math.max(1, options.habitBucketNormalisationDays);
  const commit = (field: TimeSavedAssumptionKey, value: number) => setAssumption(field, value);
  const shareBar = (sharePct: number, colour: string) => (
    <div className={styles.shareBar} title={`${formatNumber(sharePct, { maximumFractionDigits: 0 })}%`}>
      <div style={{ width: `${Math.max(0, Math.min(100, sharePct))}%`, height: '100%', backgroundColor: colour }} />
    </div>
  );

  return (
    <div className={styles.stack}>
      {/* ---------- The calculator ---------- */}
      <div ref={calculatorRef} className={mergeClasses(styles.calculator, highlighted && styles.calculatorHighlighted)}>
        <Card>
          <Text weight="semibold" size={400} className={styles.sectionTitle}>
            {t('copilotAdoptionCowork.timeSaved.model.title')}
          </Text>
          <Text size={200} className={styles.note}>
            {t('copilotAdoptionCowork.timeSaved.model.intro')}
          </Text>

          {ready && full && (
            <ScenarioPicker<Scenario>
              value={scenario}
              onChange={setScenario}
              options={[
                {
                  value: 'ready',
                  label: t(
                    plural(ready.cohortUsers, 'copilotAdoptionCowork.timeSaved.model.scenario.ready.one', 'copilotAdoptionCowork.timeSaved.model.scenario.ready.other'),
                    { users: formatCount(ready.cohortUsers) },
                  ),
                },
                {
                  value: 'full',
                  label: t(
                    plural(full.cohortUsers, 'copilotAdoptionCowork.timeSaved.model.scenario.full.one', 'copilotAdoptionCowork.timeSaved.model.scenario.full.other'),
                    { users: formatCount(full.cohortUsers) },
                  ),
                },
              ]}
            />
          )}

          <Text size={200} className={styles.note}>
            {t(
              plural(
                projection.projectedUsers,
                'copilotAdoptionCowork.timeSaved.table.caption.one',
                'copilotAdoptionCowork.timeSaved.table.caption.other',
              ),
              { users: formatCount(projection.projectedUsers) },
            )}
          </Text>

          <div className={styles.tableWrap}>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th className={styles.th}>{t('copilotAdoptionCowork.timeSaved.table.work')}</th>
                  <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionCowork.timeSaved.table.doneByHand')}</th>
                  <th className={styles.th}>{t('copilotAdoptionCowork.timeSaved.table.handedOver')}</th>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.minutesEach')}</th>
                  <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.table.hours')}</th>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.share')}</th>
                </tr>
              </thead>
              <tbody>
                {projection.activities.map((a) => {
                  const keys = COWORK_ACTIVITY_ASSUMPTIONS[a.activity];
                  return (
                    <tr key={a.activity}>
                      <td className={styles.td}>
                        <span className={styles.activityCell}>
                          <span className={styles.swatch} style={{ backgroundColor: COWORK_ACTIVITY_COLOUR[a.activity] }} aria-hidden="true" />
                          <span>
                            <Text size={300} weight="semibold">
                              {t(COWORK_ACTIVITY_LABEL[a.activity])}
                            </Text>
                            <Text size={100} className={styles.sub}>
                              {t(COWORK_ACTIVITY_VOLUME_LABEL[a.activity])}
                            </Text>
                            <Text size={100} className={styles.sub}>
                              {t(
                                plural(
                                  a.displayPieces,
                                  'copilotAdoptionCowork.timeSaved.table.handedOverCount.one',
                                  'copilotAdoptionCowork.timeSaved.table.handedOverCount.other',
                                ),
                                { pieces: formatCount(a.displayPieces) },
                              )}
                            </Text>
                          </span>
                        </span>
                      </td>
                      <td className={mergeClasses(styles.td, styles.tdNumeric)}>{formatCount(a.volume)}</td>
                      <td className={styles.td}>
                        <AssumptionInput
                          field={keys.share}
                          value={assumptions[keys.share]}
                          defaultValue={defaults[keys.share]}
                          customised={customised.includes(keys.share)}
                          label={t(COWORK_ACTIVITY_SHARE_INPUT_LABEL[a.activity])}
                          unit="%"
                          scale={100}
                          onCommit={commit}
                          onReset={resetAssumption}
                        />
                      </td>
                      <td className={styles.td}>
                        <AssumptionInput
                          field={keys.minutes}
                          value={assumptions[keys.minutes]}
                          defaultValue={defaults[keys.minutes]}
                          customised={customised.includes(keys.minutes)}
                          label={t(COWORK_ACTIVITY_MINUTES_INPUT_LABEL[a.activity])}
                          unit={t('copilotAdoptionTimeSaved.unit.minutes')}
                          onCommit={commit}
                          onReset={resetAssumption}
                        />
                      </td>
                      <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                        {t('copilotAdoptionTimeSaved.table.hoursValue', { hours: formatCount(a.displayHours) })}
                      </td>
                      <td className={styles.td}>{shareBar(a.sharePct, COWORK_ACTIVITY_COLOUR[a.activity])}</td>
                    </tr>
                  );
                })}
                {projection.observedUsers > 0 && (
                  <tr>
                    <td className={styles.td}>
                      <span className={styles.activityCell}>
                        <span className={styles.swatch} style={{ backgroundColor: COWORK_OBSERVED_COLOUR }} aria-hidden="true" />
                        <span>
                          <Text size={300} weight="semibold">
                            {t('copilotAdoptionCowork.timeSaved.activity.observedTasks')}
                          </Text>
                          <Text size={100} className={styles.sub}>
                            {t(
                              plural(
                                projection.observedUsers,
                                'copilotAdoptionCowork.timeSaved.volume.observedTasks.one',
                                'copilotAdoptionCowork.timeSaved.volume.observedTasks.other',
                              ),
                              { users: formatCount(projection.observedUsers) },
                            )}
                          </Text>
                        </span>
                      </span>
                    </td>
                    <td className={mergeClasses(styles.td, styles.tdNumeric)}>{formatCount(projection.observedTasks)}</td>
                    <td className={styles.td}>
                      <Text size={100} className={styles.sub}>
                        {t('copilotAdoptionCowork.timeSaved.table.countedAsReported')}
                      </Text>
                    </td>
                    <td className={styles.td}>
                      <AssumptionInput
                        field="taskMinutes"
                        value={assumptions.taskMinutes}
                        defaultValue={defaults.taskMinutes}
                        customised={customised.includes('taskMinutes')}
                        label={t('copilotAdoptionCowork.timeSaved.input.taskMinutes')}
                        unit={t('copilotAdoptionTimeSaved.unit.minutes')}
                        onCommit={commit}
                        onReset={resetAssumption}
                      />
                    </td>
                    <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                      {t('copilotAdoptionTimeSaved.table.hoursValue', { hours: formatCount(projection.observedDisplayHours) })}
                    </td>
                    <td className={styles.td}>{shareBar(projection.observedSharePct, COWORK_OBSERVED_COLOUR)}</td>
                  </tr>
                )}
                <tr className={styles.totalRow}>
                  <td className={styles.td} colSpan={4}>
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
                  <td className={styles.td} colSpan={3}>
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

      {/* ---------- What is and is not known ---------- */}
      <div>
        <Text weight="semibold" size={400} className={styles.sectionTitle}>
          {t('copilotAdoptionCowork.timeSaved.cowork.title')}
        </Text>
        <Text size={200} className={styles.note}>
          {tNode('copilotAdoptionCowork.timeSaved.cowork.intro', {
            cowork: (
              <Link href={COWORK_OVERVIEW_URL} target="_blank" rel="noopener noreferrer">
                {t('copilotAdoptionCowork.timeSaved.rationale.coworkLink')}
              </Link>
            ),
          })}
        </Text>
      </div>

      <div className={styles.grid}>
        {/* ---------- The minutes ---------- */}
        <Card className={styles.card} style={{ borderTopColor: TIME_SAVED_COWORK_COLOUR }}>
          <div className={styles.cardHead}>
            <Text weight="semibold" size={400}>
              {t('copilotAdoptionCowork.timeSaved.card.minutes', { minutes: formatAssumption(defaults.taskMinutes) })}
            </Text>
            {customisesAny(customised, COWORK_MINUTES_KEYS) ? (
              <Badge size="small" appearance="tint" color="brand">
                {t('copilotAdoptionTimeSaved.input.yourFigure')}
              </Badge>
            ) : (
              <AssumptionBadge size="small" />
            )}
          </div>
          <MessageBar intent="warning" className={styles.noStudy}>
            <MessageBarBody>{t('copilotAdoptionCowork.timeSaved.cowork.noStudy')}</MessageBarBody>
          </MessageBar>
          <div>
            <Text size={100} weight="semibold" className={styles.label}>
              {t('copilotAdoptionCowork.timeSaved.cowork.whatItDoes')}
            </Text>
            <ul className={styles.list}>
              {COWORK_TASK_RATIONALE.operationKeys.map((key) => (
                <li key={key}>
                  <Text size={200}>{t(key)}</Text>
                </li>
              ))}
            </ul>
          </div>
          <div>
            <Text size={100} weight="semibold" className={styles.label}>
              {t('copilotAdoptionTimeSaved.rationale.whyThisFigure', { minutes: formatAssumption(defaults.taskMinutes) })}
            </Text>
            <Text size={200}>{t(COWORK_TASK_RATIONALE.whyKey)}</Text>
          </div>
          <Text size={200}>{t('copilotAdoptionCowork.timeSaved.cowork.increment')}</Text>
          <div>
            <Text size={100} weight="semibold" className={styles.label}>
              {t('copilotAdoptionCowork.timeSaved.cowork.nearestEvidence')}
            </Text>
            <ul className={styles.evidenceList}>
              {COWORK_TASK_RATIONALE.evidence.map((item) => (
                <EvidenceEntry key={item.id} item={item} />
              ))}
            </ul>
          </div>
          <div className={styles.test}>
            <Text size={100} weight="semibold" className={styles.label}>
              {t('copilotAdoptionTimeSaved.rationale.testIt')}
            </Text>
            <Text size={200}>{t(COWORK_TASK_RATIONALE.testKey)}</Text>
          </div>
        </Card>

        {/* ---------- The shares, and the one check there is ---------- */}
        <Card className={styles.card} style={{ borderTopColor: TIME_SAVED_COWORK_COLOUR }}>
          <div className={styles.cardHead}>
            <Text weight="semibold" size={400}>
              {t('copilotAdoptionCowork.timeSaved.shares.title')}
            </Text>
            <AssumptionBadge size="small" />
          </div>
          <Text size={200}>{t('copilotAdoptionCowork.timeSaved.shares.intro')}</Text>
          <ul className={styles.list}>
            {COWORK_ACTIVITIES.map((activity) => (
              <li key={activity}>
                <Text size={200}>
                  {t(COWORK_ACTIVITY_SHARE_WHY[activity], {
                    share: percentFigure(defaults[COWORK_ACTIVITY_ASSUMPTIONS[activity].share]),
                  })}
                </Text>
              </li>
            ))}
          </ul>
          <div>
            <Text size={100} weight="semibold" className={styles.label}>
              {t('copilotAdoptionCowork.timeSaved.shares.check.title')}
            </Text>
            <Text size={200} block>
              {projection.observedRateUsers > 0
                ? t(
                    plural(
                      projection.observedRateUsers,
                      'copilotAdoptionCowork.timeSaved.shares.check.observed.one',
                      'copilotAdoptionCowork.timeSaved.shares.check.observed.other',
                    ),
                    {
                      users: formatCount(projection.observedRateUsers),
                      rate: formatModelled(projection.observedRate),
                      model: formatModelled(projection.piecesPerProjectedPerson),
                    },
                  )
                : t('copilotAdoptionCowork.timeSaved.shares.check.none', {
                    model: formatModelled(projection.piecesPerProjectedPerson),
                  })}
            </Text>
            {projection.observedRateUsers > 0 && (
              <Text size={200} block style={{ marginTop: '6px' }}>
                {t('copilotAdoptionCowork.timeSaved.shares.check.caveat')}
              </Text>
            )}
          </div>
          <div className={styles.test}>
            <Text size={100} weight="semibold" className={styles.label}>
              {t('copilotAdoptionTimeSaved.rationale.testIt')}
            </Text>
            <Text size={200}>{t('copilotAdoptionCowork.timeSaved.shares.test')}</Text>
          </div>
        </Card>
      </div>

      {/* ---------- Before you quote it ---------- */}
      <div className={styles.grid}>
        <Card>
          <Text weight="semibold" size={400}>
            {t('copilotAdoptionTimeSaved.caveats.title')}
          </Text>
          <ul className={styles.assumptionList}>
            <li key="estimate-assumption-minutes">
              <Text size={200}>
                {t('copilotAdoptionCowork.estimate.assumption.minutes', {
                  organise: formatNumber(assumptions.organiseMeetingsMinutes, { maximumFractionDigits: 15 }),
                  prepare: formatNumber(assumptions.prepareMeetingsMinutes, { maximumFractionDigits: 15 }),
                  email: formatNumber(assumptions.sendEmailMinutes, { maximumFractionDigits: 15 }),
                  teams: formatNumber(assumptions.postInTeamsMinutes, { maximumFractionDigits: 15 }),
                  documents: formatNumber(assumptions.createDocumentsMinutes, { maximumFractionDigits: 15 }),
                  tasks: formatNumber(assumptions.taskMinutes, { maximumFractionDigits: 15 }),
                })}
              </Text>
            </li>
            <li key="estimate-assumption-shares">
              <Text size={200}>
                {t('copilotAdoptionCowork.estimate.assumption.shares', {
                  organise: percentFigure(assumptions.organiseMeetingsShare),
                  prepare: percentFigure(assumptions.prepareMeetingsShare),
                  email: percentFigure(assumptions.sendEmailShare),
                  teams: percentFigure(assumptions.postInTeamsShare),
                  documents: percentFigure(assumptions.createDocumentsShare),
                })}
              </Text>
            </li>
            <li key="estimate-assumption-volumes">
              <Text size={200}>
                {t(
                  plural(
                    projection.cohortUsers,
                    'copilotAdoptionCowork.estimate.assumption.volumes.one',
                    'copilotAdoptionCowork.estimate.assumption.volumes.other',
                  ),
                  {
                    users: formatNumber(projection.cohortUsers),
                    workingDays: formatNumber(projection.workingDaysPerMonth, { maximumFractionDigits: 15 }),
                    days: formatNumber(monthDays),
                  },
                )}
              </Text>
            </li>
            <li key="estimate-assumption-observed">
              <Text size={200}>
                {projection.observedUsers > 0
                  ? t(
                      plural(
                        projection.observedUsers,
                        'copilotAdoptionCowork.estimate.assumption.observedTasks.one',
                        'copilotAdoptionCowork.estimate.assumption.observedTasks.other',
                      ),
                      {
                        tasks: formatNumber(projection.observedTasks),
                        users: formatNumber(projection.observedUsers),
                      },
                    )
                  : t('copilotAdoptionCowork.estimate.assumption.observedNone')}
              </Text>
            </li>
            <li key="estimate-assumption-increment">
              <Text size={200}>{t('copilotAdoptionCowork.estimate.assumption.increment')}</Text>
            </li>
            <li key="estimate-assumption-leftOut">
              <Text size={200}>{t('copilotAdoptionCowork.estimate.assumption.leftOut')}</Text>
            </li>
            <li key="estimate-assumption-lowerBound">
              <Text size={200}>
                {t('copilotAdoptionCowork.estimate.assumption.lowerBound', { percent: conservativePercent })}
              </Text>
            </li>
            <li key="estimate-assumption-potential">
              <Text size={200}>{t('copilotAdoptionCowork.estimate.assumption.potential')}</Text>
            </li>
            <li key="estimate-assumption-notMeasured">
              <Text size={200}>{t('copilotAdoptionCowork.estimate.assumption.notMeasured')}</Text>
            </li>
            <li key="estimate-assumption-noMoney">
              <Text size={200}>{t('copilotAdoptionCowork.estimate.assumption.noMoney')}</Text>
            </li>
          </ul>
        </Card>

        <Card>
          <Text weight="semibold" size={400}>
            {t('copilotAdoptionTimeSaved.own.title')}
          </Text>
          <ol className={styles.steps}>
            <li>
              <Text size={200}>{t('copilotAdoptionCowork.timeSaved.own.coworkIncrement')}</Text>
            </li>
            <li>
              <Text size={200}>{t('copilotAdoptionCowork.timeSaved.own.askPerTask')}</Text>
            </li>
            <li>
              <Text size={200}>{t('copilotAdoptionCowork.timeSaved.own.enter')}</Text>
            </li>
          </ol>
        </Card>
      </div>
    </div>
  );
}
