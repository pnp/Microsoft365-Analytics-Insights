import { useState } from 'react';
import { mergeClasses, Text, Card, Badge, Link, MessageBar, MessageBarBody } from '@fluentui/react-components';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { formatNumber, plural, useT, useTNode } from '../../i18n';
import { projectCoworkTimeSaved, type TimeSavedAssumptionKey, type TimeSavedAssumptionState } from './coworkTimeSaved';
import { COWORK_OVERVIEW_URL, COWORK_TASK_RATIONALE } from './coworkTimeSavedEvidence';
import {
  AssumptionBadge,
  AssumptionInput,
  CalculatorControls,
  EvidenceEntry,
  ScenarioPicker,
  TIME_SAVED_COWORK_COLOUR,
  formatAssumption,
  useCalculatorFocus,
  useModelStyles,
} from './timeSavedShared';

type Scenario = 'ready' | 'full';

/**
 * The Cowork estimate laid open: what was observed, what is assumed, and what is and is not known.
 *
 * Written for the meeting where somebody pushes back on the number. Every assumption is an input the
 * reader can change on the spot, and the page says plainly that no study has measured Cowork - so the
 * figure is sized honestly for what it is: the value of enabling Cowork for people who already hold a
 * Copilot licence, on top of what that licence gives back. There is no sense check, because there is
 * nothing published to check Cowork against; the Copilot evidence and its sense check sit with the
 * licence estimate, on the Licence opportunities tab.
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
    return (
      <Card>
        <Text weight="semibold" block>
          {t('copilotAdoptionCowork.timeSaved.model.title')}
        </Text>
        <Text size={200} className={styles.note}>
          {t('copilotAdoptionCowork.timeSaved.model.empty')}
        </Text>
      </Card>
    );
  }

  const conservativePercent = formatNumber(assumptions.conservativeRatio * 100, { maximumFractionDigits: 1 });
  const monthDays = Math.max(1, options.habitBucketNormalisationDays);
  const commit = (field: TimeSavedAssumptionKey, value: number) => setAssumption(field, value);

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

          <div className={styles.tableWrap}>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.work')}</th>
                  <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.table.observed')}</th>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.minutesEach')}</th>
                  <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.table.hours')}</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td className={styles.td}>
                    <span className={styles.activityCell}>
                      <span className={styles.swatch} style={{ backgroundColor: TIME_SAVED_COWORK_COLOUR }} aria-hidden="true" />
                      <span>
                        <Text size={300} weight="semibold">
                          {t('copilotAdoptionCowork.timeSaved.activity.coworkTasks')}
                        </Text>
                        <Text size={100} className={styles.sub}>
                          {t(
                            plural(
                              projection.projectedUsers,
                              'copilotAdoptionCowork.timeSaved.volume.coworkTasks.one',
                              'copilotAdoptionCowork.timeSaved.volume.coworkTasks.other',
                            ),
                            {
                              observed: formatCount(projection.observedTasks),
                              users: formatCount(projection.projectedUsers),
                              rate: formatAssumption(projection.tasksPerPerson),
                            },
                          )}
                        </Text>
                      </span>
                    </span>
                  </td>
                  <td className={mergeClasses(styles.td, styles.tdNumeric)}>{formatCount(projection.tasks)}</td>
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
                    {t('copilotAdoptionTimeSaved.table.hoursValue', { hours: formatCount(projection.hoursHigh) })}
                  </td>
                </tr>
                <tr>
                  <td className={styles.td} colSpan={2}>
                    <Text size={200}>
                      {t(
                        plural(
                          projection.projectedUsers,
                          'copilotAdoptionCowork.timeSaved.table.tasksPerPerson.one',
                          'copilotAdoptionCowork.timeSaved.table.tasksPerPerson.other',
                        ),
                        { users: formatCount(projection.projectedUsers) },
                      )}
                    </Text>
                  </td>
                  <td className={styles.td} colSpan={2}>
                    <AssumptionInput
                      field="tasksPerPerson"
                      value={assumptions.tasksPerPerson}
                      defaultValue={defaults.tasksPerPerson}
                      customised={customised.includes('tasksPerPerson')}
                      label={t('copilotAdoptionCowork.timeSaved.input.tasksPerPerson')}
                      unit={t('copilotAdoptionCowork.timeSaved.unit.tasks')}
                      defaultLabel={t(
                        projection.rateBasis === 'observed'
                          ? 'copilotAdoptionCowork.timeSaved.input.rateDefault.observed'
                          : 'copilotAdoptionCowork.timeSaved.input.rateDefault.assumed',
                      )}
                      onCommit={commit}
                      onReset={resetAssumption}
                    />
                  </td>
                </tr>
                <tr className={styles.totalRow}>
                  <td className={styles.td} colSpan={3}>
                    {t('copilotAdoptionTimeSaved.table.total')}
                  </td>
                  <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                    <span className={styles.bigHours}>
                      {t('copilotAdoptionTimeSaved.table.hoursValue', { hours: formatCount(projection.hoursHigh) })}
                    </span>
                  </td>
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
        <Card className={styles.card} style={{ borderTopColor: TIME_SAVED_COWORK_COLOUR }}>
          <div className={styles.cardHead}>
            <Text weight="semibold" size={400}>
              {t('copilotAdoptionCowork.timeSaved.card.coworkTasks', { minutes: formatAssumption(defaults.taskMinutes) })}
            </Text>
            {customised.includes('taskMinutes') ? (
              <Badge size="small" appearance="tint" color="brand">
                {t('copilotAdoptionTimeSaved.input.usingYours', { minutes: formatAssumption(assumptions.taskMinutes) })}
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

        <Card className={styles.card} style={{ borderTopColor: TIME_SAVED_COWORK_COLOUR }}>
          <div className={styles.cardHead}>
            <Text weight="semibold" size={400}>
              {t('copilotAdoptionCowork.timeSaved.cowork.volumeTitle', { rate: formatAssumption(projection.tasksPerPerson) })}
            </Text>
          </div>
          <Text size={200}>
            {projection.rateBasis === 'observed'
              ? t(
                  plural(
                    projection.rateUsers,
                    'copilotAdoptionCowork.timeSaved.cowork.volume.observed.one',
                    'copilotAdoptionCowork.timeSaved.cowork.volume.observed.other',
                  ),
                  { users: formatCount(projection.rateUsers) },
                )
              : t(
                  projection.rateBasis === 'custom'
                    ? 'copilotAdoptionCowork.timeSaved.cowork.volume.custom'
                    : 'copilotAdoptionCowork.timeSaved.cowork.volume.assumed',
                )}
          </Text>
          <Text size={200}>{t('copilotAdoptionCowork.timeSaved.cowork.volume.counted')}</Text>
          <Text size={200}>{t('copilotAdoptionCowork.timeSaved.cowork.increment')}</Text>
          <div className={styles.test}>
            <Text size={100} weight="semibold" className={styles.label}>
              {t('copilotAdoptionTimeSaved.rationale.testIt')}
            </Text>
            <Text size={200}>{t('copilotAdoptionCowork.timeSaved.cowork.volume.test')}</Text>
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
            <li key="estimate-assumption-taskMinutes">
              <Text size={200}>
                {t('copilotAdoptionCowork.estimate.assumption.taskMinutes', {
                  minutes: formatNumber(assumptions.taskMinutes, { maximumFractionDigits: 15 }),
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
                    days: formatNumber(monthDays),
                  },
                )}
              </Text>
            </li>
            <li key="estimate-assumption-taskRate">
              <Text size={200}>
                {projection.rateBasis === 'observed'
                  ? t(
                      plural(
                        projection.rateUsers,
                        'copilotAdoptionCowork.estimate.assumption.taskRateObserved.one',
                        'copilotAdoptionCowork.estimate.assumption.taskRateObserved.other',
                      ),
                      {
                        rate: formatNumber(projection.tasksPerPerson, { maximumFractionDigits: 15 }),
                        users: formatNumber(projection.rateUsers),
                      },
                    )
                  : projection.rateBasis === 'custom'
                    ? t('copilotAdoptionCowork.estimate.assumption.taskRateCustom', {
                        rate: formatNumber(projection.tasksPerPerson, { maximumFractionDigits: 15 }),
                      })
                    : t('copilotAdoptionCowork.estimate.assumption.taskRateAssumed', {
                        rate: formatNumber(projection.tasksPerPerson, { maximumFractionDigits: 15 }),
                      })}
              </Text>
            </li>
            <li key="estimate-assumption-increment">
              <Text size={200}>{t('copilotAdoptionCowork.estimate.assumption.increment')}</Text>
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
