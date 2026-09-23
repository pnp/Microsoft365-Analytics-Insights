import { useEffect, useId, useState, type ReactNode } from 'react';
import {
  makeStyles,
  mergeClasses,
  tokens,
  Text,
  Card,
  Input,
  Button,
  Badge,
  Link,
  Radio,
  RadioGroup,
  Tooltip,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import { ArrowCounterclockwise16Regular, Open12Regular } from '@fluentui/react-icons';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { formatNumber, plural, useT, useTNode, type TFunction, type TranslationKey } from '../../i18n';
import {
  TIME_SAVED_LIMITS,
  formatModelled,
  isValidAssumption,
  modelledRange,
  projectTimeSaved,
  type TimeSavedActivity,
  type TimeSavedAssumptionKey,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';
import {
  ACTIVITY_RATIONALE,
  CONSERVATIVE_EVIDENCE,
  COWORK_OVERVIEW_URL,
  EVIDENCE_METHOD_LABEL,
  EVIDENCE_METHOD_TOOLTIP,
  SELF_REPORT_CAVEAT,
  TIME_SAVED_BENCHMARKS,
  benchmarkRange,
  senseCheck,
  type EvidenceItem,
} from './coworkTimeSavedEvidence';
import { TIME_SAVED_ACTIVITY_COLOUR, TIME_SAVED_ACTIVITY_LABEL } from './CoworkTimeSavedHero';

const ACTIVITY_ASSUMPTION: Record<TimeSavedActivity, TimeSavedAssumptionKey> = {
  meetings: 'meetingMinutes',
  email: 'emailMinutes',
  documents: 'documentMinutes',
};

const ACTIVITY_VOLUME_LABEL: Record<TimeSavedActivity, TranslationKey> = {
  meetings: 'copilotAdoptionCowork.timeSaved.volume.meetings',
  email: 'copilotAdoptionCowork.timeSaved.volume.email',
  documents: 'copilotAdoptionCowork.timeSaved.volume.documents',
};

const ACTIVITY_INPUT_LABEL: Record<TimeSavedActivity, TranslationKey> = {
  meetings: 'copilotAdoptionCowork.timeSaved.input.meetingMinutes',
  email: 'copilotAdoptionCowork.timeSaved.input.emailMinutes',
  documents: 'copilotAdoptionCowork.timeSaved.input.documentMinutes',
};

const ACTIVITY_CARD_TITLE: Record<TimeSavedActivity, TranslationKey> = {
  meetings: 'copilotAdoptionCowork.timeSaved.card.meetings',
  email: 'copilotAdoptionCowork.timeSaved.card.email',
  documents: 'copilotAdoptionCowork.timeSaved.card.documents',
};

const useStyles = makeStyles({
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  sectionTitle: {
    display: 'block',
    marginBottom: '4px',
  },
  note: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    maxWidth: '900px',
  },
  scenario: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    margin: '12px 0 8px',
  },
  tableWrap: {
    overflowX: 'auto',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  th: {
    textAlign: 'left',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
    padding: '8px 10px',
    borderBottomWidth: '2px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke1,
    whiteSpace: 'nowrap',
  },
  thNumeric: {
    textAlign: 'right',
  },
  td: {
    padding: '10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
    verticalAlign: 'middle',
  },
  tdNumeric: {
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },
  activityCell: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  swatch: {
    width: '12px',
    height: '12px',
    borderRadius: '3px',
    flexShrink: 0,
  },
  sub: {
    display: 'block',
    color: tokens.colorNeutralForeground3,
  },
  inputCell: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
  },
  input: {
    width: '110px',
  },
  shareBar: {
    height: '8px',
    borderRadius: '4px',
    backgroundColor: tokens.colorNeutralBackground3,
    overflow: 'hidden',
    minWidth: '80px',
  },
  totalRow: {
    fontWeight: tokens.fontWeightSemibold,
  },
  bigHours: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: 700,
  },
  controlsRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
    marginTop: '12px',
  },
  inline: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))',
    gap: '16px',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    borderTopWidth: '4px',
    borderTopStyle: 'solid',
  },
  cardHead: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
  },
  label: {
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    display: 'block',
    marginBottom: '2px',
  },
  list: {
    margin: 0,
    paddingLeft: '18px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  evidenceList: {
    margin: 0,
    padding: 0,
    listStyleType: 'none',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  evidenceItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  evidenceHead: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
  },
  measured: {
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: '#107c10',
  },
  test: {
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    padding: '8px 10px',
  },
  benchmarkRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(190px, 1.7fr) minmax(90px, 1.3fr) auto',
    alignItems: 'center',
    gap: '10px',
    padding: '6px 0',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
  },
  benchmarkBarTrack: {
    position: 'relative',
    height: '10px',
    borderRadius: '5px',
    backgroundColor: tokens.colorNeutralBackground3,
  },
  benchmarkBar: {
    position: 'absolute',
    top: 0,
    bottom: 0,
    left: 0,
    borderRadius: '5px',
  },
  modelRow: {
    backgroundColor: tokens.colorBrandBackground2,
    borderRadius: tokens.borderRadiusMedium,
    padding: '6px 8px',
    borderBottomWidth: '0',
  },
  assumptionList: {
    margin: '8px 0 0',
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    color: tokens.colorNeutralForeground2,
  },
  steps: {
    margin: '6px 0 0',
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
});

function MethodBadge({ method }: { method: EvidenceItem['method'] }) {
  const styles = useStyles();
  const t = useT();
  return (
    <Tooltip relationship="description" content={t(EVIDENCE_METHOD_TOOLTIP[method])}>
      {method === 'measured' ? (
        <Badge size="small" className={styles.measured}>
          {t(EVIDENCE_METHOD_LABEL[method])}
        </Badge>
      ) : (
        <Badge size="small" appearance="outline" color={method === 'selfReported' ? 'warning' : 'informative'}>
          {t(EVIDENCE_METHOD_LABEL[method])}
        </Badge>
      )}
    </Tooltip>
  );
}

function EvidenceEntry({ item }: { item: EvidenceItem }) {
  const styles = useStyles();
  const t = useT();
  return (
    <li className={styles.evidenceItem}>
      <span className={styles.evidenceHead}>
        <MethodBadge method={item.method} />
        <Link href={item.url} target="_blank" rel="noopener noreferrer">
          {t(item.sourceKey)}
          {'\u00a0'}
          <Open12Regular aria-hidden="true" />
        </Link>
      </span>
      <Text size={200}>{t(item.findingKey)}</Text>
    </li>
  );
}

/**
 * One editable assumption.
 *
 * Holds its own draft text so a reader can type "0." on the way to "0.5" without the half-typed value
 * being rejected and snapped back under their cursor. Only a figure the model accepts is committed;
 * anything else is shown as a validation message and changes nothing.
 */
function AssumptionInput({
  field,
  value,
  defaultValue,
  customised,
  label,
  unit,
  scale = 1,
  onCommit,
  onReset,
}: {
  field: TimeSavedAssumptionKey;
  value: number;
  defaultValue: number;
  customised: boolean;
  label: string;
  unit: ReactNode;
  /** Display multiplier: the conservative ratio is edited as a percentage. */
  scale?: number;
  onCommit: (field: TimeSavedAssumptionKey, value: number) => void;
  onReset: (field: TimeSavedAssumptionKey) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const messageId = useId();
  const shown = (v: number) => String(Math.round(v * scale * 1000) / 1000);
  const [draft, setDraft] = useState(shown(value));
  const [invalid, setInvalid] = useState(false);

  // A reset, or the same figure changed elsewhere on the page, has to reach the box.
  useEffect(() => {
    setDraft(shown(value));
    setInvalid(false);
  }, [value, scale]);

  const limits = TIME_SAVED_LIMITS[field];

  return (
    <span className={styles.inputCell}>
      <Input
        className={styles.input}
        type="number"
        inputMode="decimal"
        size="small"
        value={draft}
        min={limits.min * scale}
        max={limits.max * scale}
        step="any"
        aria-label={label}
        aria-invalid={invalid || undefined}
        aria-describedby={invalid ? messageId : undefined}
        contentAfter={<Text size={200}>{unit}</Text>}
        onChange={(_e, data) => {
          setDraft(data.value);
          const parsed = data.value.trim() === '' ? Number.NaN : Number(data.value) / scale;
          if (isValidAssumption(field, parsed)) {
            setInvalid(false);
            onCommit(field, parsed);
          } else {
            setInvalid(true);
          }
        }}
        onBlur={() => {
          if (invalid) {
            setDraft(shown(value));
            setInvalid(false);
          }
        }}
      />
      {customised ? (
        <>
          <Badge size="small" appearance="tint" color="brand">
            {t('copilotAdoptionCowork.timeSaved.input.yourFigure')}
          </Badge>
          <Button
            size="small"
            appearance="subtle"
            icon={<ArrowCounterclockwise16Regular />}
            onClick={() => onReset(field)}
            data-print="hide"
          >
            {t('copilotAdoptionCowork.timeSaved.input.resetTo', { value: formatNumber(defaultValue * scale, { maximumFractionDigits: 2 }) })}
          </Button>
        </>
      ) : (
        <Text size={100} className={styles.sub}>
          {t('copilotAdoptionCowork.timeSaved.input.productDefault')}
        </Text>
      )}
      {invalid && (
        <Text id={messageId} size={100} role="alert" style={{ color: tokens.colorPaletteRedForeground1 }}>
          {t('copilotAdoptionCowork.timeSaved.input.invalid', {
            min: formatNumber(limits.min * scale),
            max: formatNumber(limits.max * scale),
          })}
        </Text>
      )}
    </span>
  );
}

type Scenario = 'full' | 'ready';

function senseCheckKey(verdict: 'below' | 'within' | 'above'): TranslationKey {
  switch (verdict) {
    case 'above':
      return 'copilotAdoptionCowork.timeSaved.senseCheck.verdict.above';
    case 'below':
      return 'copilotAdoptionCowork.timeSaved.senseCheck.verdict.below';
    default:
      return 'copilotAdoptionCowork.timeSaved.senseCheck.verdict.within';
  }
}

function volumeUnitLabel(t: TFunction, activity: TimeSavedActivity): string {
  return t(ACTIVITY_VOLUME_LABEL[activity]);
}

/**
 * The model behind the headline, laid open: what was observed, what is assumed, and why.
 *
 * Written for the meeting where somebody pushes back on the number. Every assumption is an input the
 * reader can change on the spot, every default states how it was derived from Microsoft's own
 * published credits, every piece of evidence says whether it was measured or self-reported, and the
 * result is compared with what published studies actually found. A figure that survives that is one
 * worth quoting; one that does not should not be in a board pack at all.
 */
export default function CoworkTimeSavedModel({
  summary,
  options,
  timeSaved,
}: {
  summary: CopilotAdoptionSummary;
  options: CopilotAdoptionOptions;
  timeSaved: TimeSavedAssumptionState;
}) {
  const styles = useStyles();
  const t = useT();
  const tNode = useTNode();
  const { assumptions, defaults, customised, isCustomised, setAssumption, resetAssumption, resetAll } = timeSaved;

  const full = projectTimeSaved(summary.coworkFullRolloutEstimate, assumptions, options);
  const ready = projectTimeSaved(summary.coworkValueEstimate, assumptions, options);
  const [scenario, setScenario] = useState<Scenario>(full ? 'full' : 'ready');
  const projection = (scenario === 'full' ? full : ready) ?? full ?? ready;

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
  const range = benchmarkRange();
  // Published figures are averages across everybody licensed, so the average seat holder - the full
  // adoption cohort - is the like-for-like comparison, not the heavier people ready now.
  const averageFigure = full ?? projection;
  const verdict = senseCheck(averageFigure.minutesPerPersonDayLow);
  const benchmarkScale = Math.max(
    range.max,
    averageFigure.minutesPerPersonDayHigh,
    ready?.minutesPerPersonDayHigh ?? 0,
  ) * 1.1;

  const commit = (field: TimeSavedAssumptionKey, value: number) => setAssumption(field, value);

  return (
    <div className={styles.stack}>
      {/* ---------- The calculator ---------- */}
      <Card>
        <Text weight="semibold" size={400} className={styles.sectionTitle}>
          {t('copilotAdoptionCowork.timeSaved.model.title')}
        </Text>
        <Text size={200} className={styles.note}>
          {t('copilotAdoptionCowork.timeSaved.model.intro')}
        </Text>

        {full && ready && (
          <div className={styles.scenario} data-print="hide">
            <Text size={200} weight="semibold">
              {t('copilotAdoptionCowork.timeSaved.model.scenarioLabel')}
            </Text>
            <RadioGroup
              layout="horizontal"
              value={scenario}
              onChange={(_e, data) => setScenario(data.value as Scenario)}
              aria-label={t('copilotAdoptionCowork.timeSaved.model.scenarioLabel')}
            >
              <Radio
                value="full"
                label={t(
                  plural(full.cohortUsers, 'copilotAdoptionCowork.timeSaved.model.scenario.full.one', 'copilotAdoptionCowork.timeSaved.model.scenario.full.other'),
                  { users: formatCount(full.cohortUsers) },
                )}
              />
              <Radio
                value="ready"
                label={t(
                  plural(ready.cohortUsers, 'copilotAdoptionCowork.timeSaved.model.scenario.ready.one', 'copilotAdoptionCowork.timeSaved.model.scenario.ready.other'),
                  { users: formatCount(ready.cohortUsers) },
                )}
              />
            </RadioGroup>
          </div>
        )}

        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th className={styles.th}>{t('copilotAdoptionCowork.timeSaved.table.work')}</th>
                <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionCowork.timeSaved.table.observed')}</th>
                <th className={styles.th}>{t('copilotAdoptionCowork.timeSaved.table.minutesEach')}</th>
                <th className={mergeClasses(styles.th, styles.thNumeric)}>{t('copilotAdoptionCowork.timeSaved.table.hours')}</th>
                <th className={styles.th}>{t('copilotAdoptionCowork.timeSaved.table.share')}</th>
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
                            {volumeUnitLabel(t, a.activity)}
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
                        unit={t('copilotAdoptionCowork.timeSaved.unit.minutes')}
                        onCommit={commit}
                        onReset={resetAssumption}
                      />
                    </td>
                    <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                      {t('copilotAdoptionCowork.timeSaved.table.hoursValue', { hours: formatCount(a.displayHours) })}
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
                  {t('copilotAdoptionCowork.timeSaved.table.fullAssumption')}
                </td>
                <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                  <span className={styles.bigHours}>
                    {t('copilotAdoptionCowork.timeSaved.table.hoursValue', { hours: formatCount(projection.hoursHigh) })}
                  </span>
                </td>
                <td className={styles.td} />
              </tr>
              <tr className={styles.totalRow}>
                <td className={styles.td} colSpan={2}>
                  {t('copilotAdoptionCowork.timeSaved.table.conservativeEnd')}
                </td>
                <td className={styles.td}>
                  <AssumptionInput
                    field="conservativeRatio"
                    value={assumptions.conservativeRatio}
                    defaultValue={defaults.conservativeRatio}
                    customised={customised.includes('conservativeRatio')}
                    label={t('copilotAdoptionCowork.timeSaved.input.conservativePercent')}
                    unit="%"
                    scale={100}
                    onCommit={commit}
                    onReset={resetAssumption}
                  />
                </td>
                <td className={mergeClasses(styles.td, styles.tdNumeric)}>
                  <span className={styles.bigHours}>
                    {t('copilotAdoptionCowork.timeSaved.table.hoursValue', { hours: formatCount(projection.hoursLow) })}
                  </span>
                </td>
                <td className={styles.td} />
              </tr>
            </tbody>
          </table>
        </div>

        <div className={styles.controlsRow}>
          <span className={styles.inline}>
            <Text size={200}>{t('copilotAdoptionCowork.timeSaved.input.hoursPerDayLabel')}</Text>
            <AssumptionInput
              field="hoursPerDay"
              value={assumptions.hoursPerDay}
              defaultValue={defaults.hoursPerDay}
              customised={customised.includes('hoursPerDay')}
              label={t('copilotAdoptionCowork.timeSaved.input.hoursPerDayLabel')}
              unit={t('copilotAdoptionCowork.timeSaved.unit.hours')}
              onCommit={commit}
              onReset={resetAssumption}
            />
          </span>
          {isCustomised && (
            <Button size="small" icon={<ArrowCounterclockwise16Regular />} onClick={resetAll} data-print="hide">
              {t('copilotAdoptionCowork.timeSaved.input.resetAll')}
            </Button>
          )}
        </div>

        <MessageBar intent={isCustomised ? 'warning' : 'info'} style={{ marginTop: '12px' }}>
          <MessageBarBody>
            {isCustomised
              ? t('copilotAdoptionCowork.timeSaved.storage.customised')
              : t('copilotAdoptionCowork.timeSaved.storage.defaults')}
          </MessageBarBody>
        </MessageBar>
      </Card>

      {/* ---------- Why each figure ---------- */}
      <div>
        <Text weight="semibold" size={400} className={styles.sectionTitle}>
          {t('copilotAdoptionCowork.timeSaved.rationale.title')}
        </Text>
        <Text size={200} className={styles.note}>
          {tNode('copilotAdoptionCowork.timeSaved.rationale.intro', {
            cowork: (
              <Link href={COWORK_OVERVIEW_URL} target="_blank" rel="noopener noreferrer">
                {t('copilotAdoptionCowork.timeSaved.rationale.coworkLink')}
              </Link>
            ),
          })}
        </Text>
      </div>

      <div className={styles.grid}>
        {ACTIVITY_RATIONALE.map((r) => {
          const field = ACTIVITY_ASSUMPTION[r.activity];
          const defaultMinutes = formatNumber(defaults[field], { maximumFractionDigits: 2 });
          return (
            <Card key={r.activity} className={styles.card} style={{ borderTopColor: TIME_SAVED_ACTIVITY_COLOUR[r.activity] }}>
              <div className={styles.cardHead}>
                <Text weight="semibold" size={400}>
                  {t(ACTIVITY_CARD_TITLE[r.activity], { minutes: defaultMinutes })}
                </Text>
                {customised.includes(field) && (
                  <Badge size="small" appearance="tint" color="brand">
                    {t('copilotAdoptionCowork.timeSaved.input.usingYours', {
                      minutes: formatNumber(assumptions[field], { maximumFractionDigits: 2 }),
                    })}
                  </Badge>
                )}
              </div>
              <div>
                <Text size={100} weight="semibold" className={styles.label}>
                  {t('copilotAdoptionCowork.timeSaved.rationale.whatSavesTime')}
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
                  {t('copilotAdoptionCowork.timeSaved.rationale.whyThisFigure', { minutes: defaultMinutes })}
                </Text>
                <Text size={200}>{t(r.whyKey)}</Text>
              </div>
              <div>
                <Text size={100} weight="semibold" className={styles.label}>
                  {t('copilotAdoptionCowork.timeSaved.rationale.evidence')}
                </Text>
                <ul className={styles.evidenceList}>
                  {r.evidence.map((item) => (
                    <EvidenceEntry key={item.id} item={item} />
                  ))}
                </ul>
              </div>
              <div className={styles.test}>
                <Text size={100} weight="semibold" className={styles.label}>
                  {t('copilotAdoptionCowork.timeSaved.rationale.testIt')}
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
            {t('copilotAdoptionCowork.timeSaved.conservative.title', { percent: conservativePercent })}
          </Text>
          <Text size={200}>{t('copilotAdoptionCowork.timeSaved.conservative.why')}</Text>
          <ul className={styles.evidenceList}>
            {CONSERVATIVE_EVIDENCE.map((item) => (
              <EvidenceEntry key={item.id} item={item} />
            ))}
          </ul>
        </Card>

        {/* ---------- The sense check ---------- */}
        <Card className={styles.card} style={{ borderTopColor: tokens.colorBrandStroke1 }}>
          <Text weight="semibold" size={400}>
            {t('copilotAdoptionCowork.timeSaved.senseCheck.title')}
          </Text>
          <Text size={200}>
            {t('copilotAdoptionCowork.timeSaved.senseCheck.intro', {
              range: modelledRange(
                t,
                formatModelled(averageFigure.minutesPerPersonDayLow),
                formatModelled(averageFigure.minutesPerPersonDayHigh),
              ),
            })}
          </Text>

          <div role="list" aria-label={t('copilotAdoptionCowork.timeSaved.senseCheck.title')}>
            {[
              {
                id: 'model-full',
                label: t('copilotAdoptionCowork.timeSaved.senseCheck.yourModelAverage'),
                minutes: averageFigure.minutesPerPersonDayHigh,
                low: averageFigure.minutesPerPersonDayLow,
                model: true,
              },
              ...(full && ready
                ? [
                    {
                      id: 'model-ready',
                      label: t('copilotAdoptionCowork.timeSaved.senseCheck.yourModelReady'),
                      minutes: ready.minutesPerPersonDayHigh,
                      low: ready.minutesPerPersonDayLow,
                      model: true,
                    },
                  ]
                : []),
            ].map((row) => (
              <div key={row.id} role="listitem" className={mergeClasses(styles.benchmarkRow, styles.modelRow)}>
                <Text size={200} weight="semibold">
                  {row.label}
                </Text>
                <div className={styles.benchmarkBarTrack} aria-hidden="true">
                  <div
                    className={styles.benchmarkBar}
                    style={{
                      width: `${Math.min(100, (row.minutes / benchmarkScale) * 100)}%`,
                      backgroundColor: tokens.colorBrandBackground,
                      opacity: 0.35,
                    }}
                  />
                  <div
                    className={styles.benchmarkBar}
                    style={{
                      width: `${Math.min(100, (row.low / benchmarkScale) * 100)}%`,
                      backgroundColor: tokens.colorBrandBackground,
                    }}
                  />
                </div>
                <Text size={200} weight="semibold" style={{ whiteSpace: 'nowrap' }}>
                  {t('copilotAdoptionCowork.timeSaved.senseCheck.minutesRange', {
                    range: modelledRange(t, formatModelled(row.low), formatModelled(row.minutes)),
                  })}
                </Text>
              </div>
            ))}
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
                      backgroundColor: b.method === 'measured' ? '#107c10' : tokens.colorNeutralForeground3,
                    }}
                  />
                </div>
                <Text size={200} style={{ whiteSpace: 'nowrap' }}>
                  {t('copilotAdoptionCowork.timeSaved.senseCheck.minutes', { minutes: formatNumber(b.minutesPerDay) })}
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
          {full && ready && (
            <Text size={100} className={styles.sub}>
              {t('copilotAdoptionCowork.timeSaved.senseCheck.readyNote')}
            </Text>
          )}
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
            {t('copilotAdoptionCowork.timeSaved.caveats.title')}
          </Text>
          <ul className={styles.assumptionList}>
            <li key="estimate-assumption-saves">
              <Text size={200}>
                {t('copilotAdoptionCowork.estimate.assumption.saves', {
                  meetingMinutes: formatNumber(assumptions.meetingMinutes, { maximumFractionDigits: 15 }),
                  emailMinutes: formatNumber(assumptions.emailMinutes, { maximumFractionDigits: 15 }),
                  documentMinutes: formatNumber(assumptions.documentMinutes, { maximumFractionDigits: 15 }),
                })}
              </Text>
            </li>
            <li key="estimate-assumption-lowerBound">
              <Text size={200}>
                {t('copilotAdoptionCowork.estimate.assumption.lowerBound', {
                  percent: conservativePercent,
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
                  },
                )}
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
            {t('copilotAdoptionCowork.timeSaved.own.title')}
          </Text>
          <ol className={styles.steps}>
            <li>
              <Text size={200}>{t('copilotAdoptionCowork.timeSaved.own.pilot')}</Text>
            </li>
            <li>
              <Text size={200}>{t('copilotAdoptionCowork.timeSaved.own.askPerItem')}</Text>
            </li>
            <li>
              <Text size={200}>{t('copilotAdoptionCowork.timeSaved.own.compareDashboard')}</Text>
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
