import { makeStyles, mergeClasses, Card, MessageBar, MessageBarBody, Text } from '@fluentui/react-components';
import type { CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import { formatNumber, plural, useT, type TranslationKey } from '../../i18n';
import {
  SEAT_HOLDER_ACTION_ASSUMPTION,
  modelledRange,
  projectSeatHolderSegment,
  projectSeatHolderTimeSaved,
  type SeatHolderAction,
  type TimeSavedAssumptionKey,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';
import {
  AssumptionInput,
  CalculatorControls,
  ModelledBadge,
  TIME_SAVED_ACTIVITY_COLOUR,
  useModelStyles,
} from './timeSavedShared';

/**
 * One hue per kind of action, borrowed from the licence calculator's kinds of work so the two tables
 * read alike: Outlook is email's purple, the Office apps documents' teal, meetings the meeting blue.
 * The uncredited actions are a neutral grey, because by default they are credited with nothing.
 */
const SEAT_ACTION_COLOUR: Record<SeatHolderAction, string> = {
  outlook: TIME_SAVED_ACTIVITY_COLOUR.email,
  office: TIME_SAVED_ACTIVITY_COLOUR.documents,
  meetings: TIME_SAVED_ACTIVITY_COLOUR.meetings,
  other: '#8a8886',
};

const SEAT_ACTION_LABEL: Record<SeatHolderAction, TranslationKey> = {
  outlook: 'copilotAdoptionTimeSaved.seat.action.outlook',
  office: 'copilotAdoptionTimeSaved.seat.action.office',
  meetings: 'copilotAdoptionTimeSaved.seat.action.meetings',
  other: 'copilotAdoptionTimeSaved.seat.action.other',
};

const SEAT_ACTION_VOLUME_LABEL: Record<SeatHolderAction, TranslationKey> = {
  outlook: 'copilotAdoptionTimeSaved.seat.volume.outlook',
  office: 'copilotAdoptionTimeSaved.seat.volume.office',
  meetings: 'copilotAdoptionTimeSaved.seat.volume.meetings',
  other: 'copilotAdoptionTimeSaved.seat.volume.other',
};

const SEAT_ACTION_INPUT_LABEL: Record<SeatHolderAction, TranslationKey> = {
  outlook: 'copilotAdoptionTimeSaved.seat.input.outlook',
  office: 'copilotAdoptionTimeSaved.seat.input.office',
  meetings: 'copilotAdoptionTimeSaved.seat.input.meeting',
  other: 'copilotAdoptionTimeSaved.seat.input.other',
};

const useStyles = makeStyles({
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
    marginBottom: '4px',
  },
  notice: {
    marginTop: '12px',
  },
  footnote: {
    marginTop: '12px',
  },
});

/**
 * The seat holders' time already saved, laid open: what was observed, what each action is credited
 * with, and what that adds up to - the Licensed users tab's Time saved section.
 *
 * Deliberately the licence calculator's twin - the same table, the same editable figures with their
 * product defaults beside them, the same full and conservative totals - so a reader who has learned
 * one can read the other. It differs only where the model does: the volumes are Copilot actions
 * already taken by people who hold a licence, counted from the audit log, not work that a licence
 * might help with.
 *
 * Hours only, never money, and never added to the licensing or Cowork estimates.
 */
export default function SeatHolderTimeSavedModel({
  summary,
  timeSaved,
}: {
  summary: CopilotAdoptionSummary;
  timeSaved: TimeSavedAssumptionState;
}) {
  const styles = useModelStyles();
  const local = useStyles();
  const t = useT();
  const { assumptions, defaults, customised, setAssumption, resetAssumption } = timeSaved;

  const estimate = summary.seatHolderTimeSavedEstimate;
  const projection = projectSeatHolderTimeSaved(estimate, assumptions);
  if (!estimate || !projection) return null;

  const options = summary.options;
  const commit = (field: TimeSavedAssumptionKey, value: number) => setAssumption(field, value);
  const excluded = projection.excludedUsageReportSourcedUsers;
  const excludedText =
    excluded > 0
      ? t(plural(excluded, 'copilotAdoptionTimeSaved.seat.model.excluded.one', 'copilotAdoptionTimeSaved.seat.model.excluded.other'), {
          users: formatCount(excluded),
        })
      : null;

  const departments = (estimate.byDepartment ?? []).map((row) => ({ row, ...projectSeatHolderSegment(row, assumptions) }));
  const hoursCell = (low: number, high: number) =>
    t('copilotAdoptionTimeSaved.table.hoursValue', { hours: modelledRange(t, formatCount(low), formatCount(high)) });

  return (
    <div className={styles.stack}>
      {/* ---------- The calculator ---------- */}
      <Card>
        <div className={local.titleRow}>
          <Text weight="semibold" size={400}>
            {t('copilotAdoption.page.kpi.seatHolderTimeSaved.label')}
          </Text>
          <ModelledBadge />
        </div>

        {projection.cohortUsers > 0 ? (
          <>
            <Text size={200} className={styles.note}>
              {t(
                plural(
                  projection.cohortUsers,
                  'copilotAdoptionTimeSaved.seat.model.intro.one',
                  'copilotAdoptionTimeSaved.seat.model.intro.other',
                ),
                { users: formatCount(projection.cohortUsers) },
              )}
            </Text>

            <div className={styles.tableWrap} style={{ marginTop: '12px' }}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th className={styles.th}>{t('copilotAdoptionTimeSaved.seat.table.action')}</th>
                    <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.seat.table.observed')}</th>
                    <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.minutesEach')}</th>
                    <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.table.hours')}</th>
                    <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.share')}</th>
                  </tr>
                </thead>
                <tbody>
                  {projection.actions.map((a) => {
                    const field = SEAT_HOLDER_ACTION_ASSUMPTION[a.action];
                    return (
                      <tr key={a.action}>
                        <td className={styles.td}>
                          <span className={styles.activityCell}>
                            <span className={styles.swatch} style={{ backgroundColor: SEAT_ACTION_COLOUR[a.action] }} aria-hidden="true" />
                            <span>
                              <Text size={300} weight="semibold">
                                {t(SEAT_ACTION_LABEL[a.action])}
                              </Text>
                              <Text size={100} className={styles.sub}>
                                {t(SEAT_ACTION_VOLUME_LABEL[a.action])}
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
                            label={t(SEAT_ACTION_INPUT_LABEL[a.action])}
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
                                backgroundColor: SEAT_ACTION_COLOUR[a.action],
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

            {excludedText && (
              <Text size={200} className={mergeClasses(styles.note, local.footnote)}>
                {excludedText}
              </Text>
            )}
            <Text size={200} className={mergeClasses(styles.note, local.footnote)}>
              {t('copilotAdoptionTimeSaved.seat.model.zeroCreditNote')}
            </Text>

            <CalculatorControls
              timeSaved={timeSaved}
              showHoursPerDay={false}
              sharedNote={t('copilotAdoptionTimeSaved.seat.model.sharedNote')}
            />
          </>
        ) : (
          // Nobody has the per-action detail the model needs, so there is nothing to calculate - only
          // the reason to give.
          excludedText && (
            <MessageBar intent="info" className={local.notice}>
              <MessageBarBody>{excludedText}</MessageBarBody>
            </MessageBar>
          )
        )}
      </Card>

      {/* ---------- By department ---------- */}
      {projection.cohortUsers > 0 && departments.length > 0 && (
        <Card>
          <Text weight="semibold" size={400} className={styles.sectionTitle}>
            {t('copilotAdoptionTimeSaved.seat.departments.title')}
          </Text>
          <Text size={200} className={styles.note}>
            {t('copilotAdoptionTimeSaved.seat.departments.note', {
              top: formatCount(options.topSegments),
              minSeats: formatCount(options.minSeatsPerSegment),
            })}
          </Text>
          <div className={styles.tableWrap} style={{ marginTop: '12px' }}>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.seat.departments.department')}</th>
                  <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.seat.departments.people')}</th>
                  <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionTimeSaved.table.hours')}</th>
                  <th className={styles.th}>{t('copilotAdoptionTimeSaved.table.share')}</th>
                </tr>
              </thead>
              <tbody>
                {departments.map(({ row, hoursLow, hoursHigh }) => {
                  const sharePct = projection.hoursHigh > 0 ? (hoursHigh / projection.hoursHigh) * 100 : 0;
                  return (
                    <tr key={row.segment}>
                      <td className={styles.td}>
                        <Text size={300} weight="semibold">
                          {serverPlaceholderText(t, row.segment)}
                        </Text>
                      </td>
                      <td className={mergeClasses(styles.td, styles.tdNumeric)}>{formatCount(row.cohortUsers)}</td>
                      <td className={mergeClasses(styles.td, styles.tdNumeric)}>{hoursCell(hoursLow, hoursHigh)}</td>
                      <td className={styles.td}>
                        <div className={styles.shareBar} title={`${formatNumber(sharePct, { maximumFractionDigits: 0 })}%`}>
                          <div
                            style={{
                              width: `${Math.max(0, Math.min(100, sharePct))}%`,
                              height: '100%',
                              backgroundColor: TIME_SAVED_ACTIVITY_COLOUR.meetings,
                            }}
                          />
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </Card>
      )}
    </div>
  );
}
