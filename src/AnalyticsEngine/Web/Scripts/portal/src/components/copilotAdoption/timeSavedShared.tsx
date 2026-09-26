import { useEffect, useId, useRef, useState, type ReactNode, type RefObject } from 'react';
import {
  makeStyles,
  tokens,
  Text,
  Badge,
  Button,
  Input,
  Link,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Tooltip,
} from '@fluentui/react-components';
import { ArrowCounterclockwise16Regular, Open12Regular } from '@fluentui/react-icons';
import InfoTip from '../shared/InfoTip';
import type { InfoTipContent } from '../shared/InfoTip';
import type { CoworkActivity } from '../../types/copilotAdoption';
import { formatNumber, useT, type TranslationKey } from '../../i18n';
import {
  TIME_SAVED_LIMITS,
  isValidAssumption,
  type TimeSavedActivity,
  type TimeSavedAssumptionKey,
  type TimeSavedAssumptionState,
} from './coworkTimeSaved';
import { EVIDENCE_METHOD_LABEL, EVIDENCE_METHOD_TOOLTIP, type EvidenceItem } from './coworkTimeSavedEvidence';
import { revealElement } from './adoptionShared';

/**
 * What the two time-saved surfaces share: the licence estimate on the Licence opportunities tab and
 * the Cowork estimate on the Cowork tab look alike on purpose - the same headline frame, the same
 * calculator, the same way of citing evidence - so a reader who has learned one can read the other,
 * and so neither can drift into presenting a model differently from its sibling.
 */

/** One hue per kind of work, used by the licence headline's bar and its calculator so the two read as one. */
export const TIME_SAVED_ACTIVITY_COLOUR: Record<TimeSavedActivity, string> = {
  meetings: '#0f6cbd',
  email: '#8764b8',
  documents: '#038387',
};

/**
 * Cowork's hue - deliberately outside the Copilot family of blues, purple and teal, so the estimate
 * resting on an assumption rather than on published evidence is never mistaken for the other one.
 */
export const TIME_SAVED_COWORK_COLOUR = '#c239b3';

/**
 * One hue per kind of work Cowork could take on, used by the Cowork headline's bar and its calculator
 * so the two read as one: a warm family around Cowork's magenta, kept apart from the licence
 * estimate's. The Cowork tasks already running are a neutral grey, because they are counted, not
 * modelled from anyone's work.
 */
export const COWORK_ACTIVITY_COLOUR: Record<CoworkActivity, string> = {
  organiseMeetings: '#c239b3',
  prepareMeetings: '#77004d',
  sendEmail: '#da3b01',
  postInTeams: '#e43ba6',
  createDocuments: '#8e562e',
};

export const COWORK_OBSERVED_COLOUR = '#605e5c';

export const COWORK_ACTIVITY_LABEL: Record<CoworkActivity, TranslationKey> = {
  organiseMeetings: 'copilotAdoptionCowork.timeSaved.activity.organiseMeetings',
  prepareMeetings: 'copilotAdoptionCowork.timeSaved.activity.prepareMeetings',
  sendEmail: 'copilotAdoptionCowork.timeSaved.activity.sendEmail',
  postInTeams: 'copilotAdoptionCowork.timeSaved.activity.postInTeams',
  createDocuments: 'copilotAdoptionCowork.timeSaved.activity.createDocuments',
};

/** What the observed volume of each kind of work counts, and where it comes from. */
export const COWORK_ACTIVITY_VOLUME_LABEL: Record<CoworkActivity, TranslationKey> = {
  organiseMeetings: 'copilotAdoptionCowork.timeSaved.activityVolume.organiseMeetings',
  prepareMeetings: 'copilotAdoptionCowork.timeSaved.activityVolume.prepareMeetings',
  sendEmail: 'copilotAdoptionCowork.timeSaved.activityVolume.sendEmail',
  postInTeams: 'copilotAdoptionCowork.timeSaved.activityVolume.postInTeams',
  createDocuments: 'copilotAdoptionCowork.timeSaved.activityVolume.createDocuments',
};

export const COWORK_ACTIVITY_SHARE_INPUT_LABEL: Record<CoworkActivity, TranslationKey> = {
  organiseMeetings: 'copilotAdoptionCowork.timeSaved.input.share.organiseMeetings',
  prepareMeetings: 'copilotAdoptionCowork.timeSaved.input.share.prepareMeetings',
  sendEmail: 'copilotAdoptionCowork.timeSaved.input.share.sendEmail',
  postInTeams: 'copilotAdoptionCowork.timeSaved.input.share.postInTeams',
  createDocuments: 'copilotAdoptionCowork.timeSaved.input.share.createDocuments',
};

export const COWORK_ACTIVITY_MINUTES_INPUT_LABEL: Record<CoworkActivity, TranslationKey> = {
  organiseMeetings: 'copilotAdoptionCowork.timeSaved.input.minutes.organiseMeetings',
  prepareMeetings: 'copilotAdoptionCowork.timeSaved.input.minutes.prepareMeetings',
  sendEmail: 'copilotAdoptionCowork.timeSaved.input.minutes.sendEmail',
  postInTeams: 'copilotAdoptionCowork.timeSaved.input.minutes.postInTeams',
  createDocuments: 'copilotAdoptionCowork.timeSaved.input.minutes.createDocuments',
};

/** The badge colour for measured evidence and observed use, as elsewhere on the page. */
export const EVIDENCE_GREEN = '#107c10';

export const TIME_SAVED_ACTIVITY_LABEL: Record<TimeSavedActivity, TranslationKey> = {
  meetings: 'copilotAdoptionTimeSaved.activity.meetings',
  email: 'copilotAdoptionTimeSaved.activity.email',
  documents: 'copilotAdoptionTimeSaved.activity.documents',
};

export const ACTIVITY_ASSUMPTION: Record<TimeSavedActivity, TimeSavedAssumptionKey> = {
  meetings: 'meetingMinutes',
  email: 'emailMinutes',
  documents: 'documentMinutes',
};

export const ACTIVITY_VOLUME_LABEL: Record<TimeSavedActivity, TranslationKey> = {
  meetings: 'copilotAdoptionTimeSaved.volume.meetings',
  email: 'copilotAdoptionTimeSaved.volume.email',
  documents: 'copilotAdoptionTimeSaved.volume.documents',
};

export const ACTIVITY_INPUT_LABEL: Record<TimeSavedActivity, TranslationKey> = {
  meetings: 'copilotAdoptionTimeSaved.input.meetingMinutes',
  email: 'copilotAdoptionTimeSaved.input.emailMinutes',
  documents: 'copilotAdoptionTimeSaved.input.documentMinutes',
};

export const ACTIVITY_CARD_TITLE: Record<TimeSavedActivity, TranslationKey> = {
  meetings: 'copilotAdoptionTimeSaved.card.meetings',
  email: 'copilotAdoptionTimeSaved.card.email',
  documents: 'copilotAdoptionTimeSaved.card.documents',
};

/** A figure the page quotes with two decimals at most - an assumption, not a measurement. */
export function formatAssumption(value: number): string {
  return formatNumber(value, { maximumFractionDigits: 2 });
}

/**
 * Shares of a total as whole percentages that add up to 100. Rounded by largest remainder for the
 * same reason the hours are - "33% + 33% + 33%" under a bar that is visibly full reads as a mistake.
 */
export function wholeShares(shares: number[]): number[] {
  const total = shares.reduce((sum, s) => sum + s, 0);
  if (total <= 0) return shares.map(() => 0);
  const floors = shares.map((s) => Math.floor(s));
  let leftover = 100 - floors.reduce((sum, s) => sum + s, 0);
  const order = shares
    .map((s, index) => ({ index, remainder: s - Math.floor(s) }))
    .sort((a, b) => b.remainder - a.remainder || a.index - b.index);
  for (let i = 0; leftover > 0 && i < order.length; i++) {
    floors[order[i].index] += 1;
    leftover -= 1;
  }
  return floors;
}

// ---------------------------------------------------------------------------------------------
// The headline
// ---------------------------------------------------------------------------------------------

const useHeroStyles = makeStyles({
  hero: {
    position: 'relative',
    borderRadius: tokens.borderRadiusXLarge,
    padding: '22px 24px 18px',
    backgroundColor: tokens.colorNeutralBackground1,
    backgroundImage: `linear-gradient(120deg, ${tokens.colorBrandBackground2} 0%, ${tokens.colorNeutralBackground1} 62%)`,
    borderLeftWidth: '6px',
    borderLeftStyle: 'solid',
    boxShadow: tokens.shadow4,
    marginBottom: '16px',
    // Keeps the brand wash on paper: browsers drop background colours when printing by default, and
    // the headline without its panel reads as body text.
    printColorAdjust: 'exact',
    WebkitPrintColorAdjust: 'exact',
  },
  top: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  eyebrow: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    color: tokens.colorBrandForeground1,
    textTransform: 'uppercase',
    letterSpacing: '0.06em',
  },
  badges: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
  },
  headline: {
    display: 'block',
    marginTop: '10px',
    fontSize: '44px',
    lineHeight: '52px',
    fontWeight: 700,
    color: tokens.colorBrandForeground1,
    fontVariantNumeric: 'tabular-nums',
    letterSpacing: '-0.01em',
  },
  subline: {
    display: 'block',
    marginTop: '2px',
    color: tokens.colorNeutralForeground2,
  },
  caption: {
    display: 'block',
    marginTop: '6px',
    color: tokens.colorNeutralForeground3,
    maxWidth: '820px',
  },
  notice: {
    marginTop: '12px',
  },
  stats: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(210px, 1fr))',
    gap: '12px',
    marginTop: '18px',
  },
  stat: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: '12px 14px',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  statValue: {
    fontSize: '26px',
    lineHeight: '32px',
    fontWeight: 700,
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground1,
  },
  statLabel: {
    color: tokens.colorNeutralForeground2,
    fontWeight: tokens.fontWeightSemibold,
  },
  statHint: {
    color: tokens.colorNeutralForeground3,
  },
  basis: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
    marginTop: '16px',
    paddingTop: '12px',
    borderTopWidth: '1px',
    borderTopStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
  },
  basisText: {
    color: tokens.colorNeutralForeground2,
    maxWidth: '760px',
  },
  actions: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
  },
});

export interface HeroStat {
  key: string;
  value: string;
  label: string;
  hint?: string;
}

/**
 * The frame both headlines share: an eyebrow with its badges and definition, one large range, what it
 * covers, the restatements beside it, and the assumptions it rests on with the controls to change them.
 *
 * Built to be the number an executive remembers and the number that survives a challenge, which pull
 * in opposite directions - so it does both on the same surface. The figure is large, first and
 * restated in units people feel; and it is always a range, always badged as modelled, and always
 * printed with the exact assumptions that produced it, one click from the evidence behind them.
 */
export function TimeSavedHeroFrame({
  id,
  accent,
  eyebrow,
  badges,
  infoTitle,
  info,
  headline,
  subline,
  caption,
  notice,
  stats,
  breakdown,
  basis,
  actions,
  footer,
}: {
  id: string;
  /** The colour of the frame's left edge - Copilot's blue or Cowork's magenta. */
  accent: string;
  eyebrow: string;
  badges: ReactNode;
  infoTitle: string;
  info: InfoTipContent;
  headline: string;
  subline: string;
  caption?: string;
  notice?: ReactNode;
  stats: HeroStat[];
  breakdown?: ReactNode;
  basis: string;
  actions: ReactNode;
  footer?: ReactNode;
}) {
  const styles = useHeroStyles();

  return (
    <section
      className={styles.hero}
      style={{ borderLeftColor: accent }}
      aria-labelledby={id}
      data-print="keep-with-next"
    >
      <div className={styles.top}>
        <Text size={200} weight="semibold" className={styles.eyebrow}>
          {eyebrow}
        </Text>
        <div className={styles.badges}>
          {badges}
          <InfoTip title={infoTitle} content={info} />
        </div>
      </div>

      <span id={id} className={styles.headline}>
        {headline}
      </span>
      <Text size={400} className={styles.subline}>
        {subline}
      </Text>
      {caption && (
        <Text size={200} className={styles.caption}>
          {caption}
        </Text>
      )}
      {notice && <div className={styles.notice}>{notice}</div>}

      {stats.length > 0 && (
        <div className={styles.stats}>
          {stats.map((stat) => (
            <div key={stat.key} className={styles.stat}>
              <span className={styles.statValue}>{stat.value}</span>
              <Text size={200} className={styles.statLabel}>
                {stat.label}
              </Text>
              {stat.hint && (
                <Text size={100} className={styles.statHint}>
                  {stat.hint}
                </Text>
              )}
            </div>
          ))}
        </div>
      )}

      {breakdown}

      <div className={styles.basis}>
        <Text size={200} className={styles.basisText}>
          {basis}
        </Text>
        <div className={styles.actions} data-print="hide">
          {actions}
        </div>
      </div>

      {footer}
    </section>
  );
}

/** The modelled-not-measured badge every time-saved figure carries. */
export function ModelledBadge() {
  const t = useT();
  return (
    <Tooltip relationship="description" content={t('copilotAdoptionTimeSaved.badge.modelledTooltip')}>
      <Badge appearance="outline" color="informative" size="medium">
        {t('copilotAdoptionTimeSaved.badge.modelled')}
      </Badge>
    </Tooltip>
  );
}

/** The badge saying the minutes rest on published studies - the licence estimate's. */
export function PublishedEvidenceBadge({ size = 'medium' }: { size?: 'small' | 'medium' }) {
  const t = useT();
  return (
    <Tooltip relationship="description" content={t('copilotAdoptionTimeSaved.badge.publishedTooltip')}>
      <Badge size={size} style={{ color: tokens.colorNeutralForegroundOnBrand, backgroundColor: EVIDENCE_GREEN }}>
        {t('copilotAdoptionTimeSaved.badge.published')}
      </Badge>
    </Tooltip>
  );
}

/** The badge saying the minutes rest on no study at all - the Cowork estimate's. */
export function AssumptionBadge({ size = 'medium' }: { size?: 'small' | 'medium' }) {
  const t = useT();
  return (
    <Tooltip relationship="description" content={t('copilotAdoptionCowork.timeSaved.badge.assumptionTooltip')}>
      <Badge size={size} appearance="outline" color="warning">
        {t('copilotAdoptionCowork.timeSaved.badge.assumption')}
      </Badge>
    </Tooltip>
  );
}

// ---------------------------------------------------------------------------------------------
// The calculator and the evidence
// ---------------------------------------------------------------------------------------------

export const useModelStyles = makeStyles({
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  // The calculator, as the target of "Adjust the assumptions". The ring is what tells the reader the
  // button worked when the table was already on screen and there was nothing to scroll.
  calculator: {
    scrollMarginTop: '12px',
    borderRadius: tokens.borderRadiusMedium,
    transitionProperty: 'box-shadow',
    transitionDuration: tokens.durationSlow,
    transitionTimingFunction: tokens.curveEasyEase,
  },
  calculatorHighlighted: {
    boxShadow: `0 0 0 3px ${tokens.colorBrandStroke1}`,
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
  test: {
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    padding: '8px 10px',
  },
  noStudy: {
    marginTop: '4px',
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
  inputCell: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
  },
  input: {
    width: '110px',
  },
  /** An assumption's value as text, for paper - where the box it is typed into is not printed. */
  printedValue: {
    display: 'none',
  },
  /** The scenario picker's choice as text, for paper. Spaced like the picker it stands in for. */
  printedScenario: {
    display: 'none',
    margin: '12px 0 8px',
  },
});

export function MethodBadge({ method }: { method: EvidenceItem['method'] }) {
  const t = useT();
  return (
    <Tooltip relationship="description" content={t(EVIDENCE_METHOD_TOOLTIP[method])}>
      {method === 'measured' ? (
        <Badge size="small" style={{ color: tokens.colorNeutralForegroundOnBrand, backgroundColor: EVIDENCE_GREEN }}>
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

export function EvidenceEntry({ item }: { item: EvidenceItem }) {
  const styles = useModelStyles();
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
export function AssumptionInput({
  field,
  value,
  defaultValue,
  customised,
  label,
  unit,
  scale = 1,
  defaultLabel,
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
  /**
   * What an unchanged figure is. "Product default" for everything the product configures; the Cowork
   * task rate says instead where it came from, because it is usually observed rather than configured.
   */
  defaultLabel?: string;
  onCommit: (field: TimeSavedAssumptionKey, value: number) => void;
  onReset: (field: TimeSavedAssumptionKey) => void;
}) {
  const styles = useModelStyles();
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
      {/* A box to type into is no use on paper, so the printout states the figure instead. The
          wrapper carries the attribute because Input puts native props on its inner <input>,
          which would hide the text and leave the empty border behind. */}
      <span data-print="hide">
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
      </span>
      <Text size={200} weight="semibold" className={styles.printedValue} data-print="only">
        {formatAssumption(value * scale)} {unit}
      </Text>
      {customised ? (
        <>
          <Badge size="small" appearance="tint" color="brand">
            {t('copilotAdoptionTimeSaved.input.yourFigure')}
          </Badge>
          <Button
            size="small"
            appearance="subtle"
            icon={<ArrowCounterclockwise16Regular />}
            onClick={() => onReset(field)}
            data-print="hide"
          >
            {t('copilotAdoptionTimeSaved.input.resetTo', { value: formatAssumption(defaultValue * scale) })}
          </Button>
        </>
      ) : (
        <Text size={100} className={styles.sub}>
          {defaultLabel ?? t('copilotAdoptionTimeSaved.input.productDefault')}
        </Text>
      )}
      {invalid && (
        <Text id={messageId} size={100} role="alert" style={{ color: tokens.colorPaletteRedForeground1 }}>
          {t('copilotAdoptionTimeSaved.input.invalid', {
            min: formatNumber(limits.min * scale),
            max: formatNumber(limits.max * scale),
          })}
        </Text>
      )}
    </span>
  );
}

/**
 * The calculator's reaction to "Adjust the assumptions": each new request scrolls it into view,
 * focuses its first figure and briefly rings it - so the button visibly does something even when the
 * section was already open and the table already on screen.
 */
export function useCalculatorFocus(focusRequest: number): { ref: RefObject<HTMLDivElement | null>; highlighted: boolean } {
  const ref = useRef<HTMLDivElement>(null);
  const [highlighted, setHighlighted] = useState(false);

  useEffect(() => {
    if (!focusRequest) return undefined;
    const calculator = ref.current;
    if (!calculator) return undefined;

    revealElement(calculator);
    // preventScroll: focusing would otherwise jump straight to the field and cut the smooth scroll
    // short. The first number input is the calculator's first figure.
    calculator.querySelector<HTMLInputElement>('input[type="number"]')?.focus({ preventScroll: true });
    setHighlighted(true);
    const timer = window.setTimeout(() => setHighlighted(false), 1600);
    return () => window.clearTimeout(timer);
  }, [focusRequest]);

  return { ref, highlighted };
}

/** A pair of cohorts the calculator can show its working for. */
export function ScenarioPicker<T extends string>({
  value,
  options,
  onChange,
}: {
  value: T;
  options: Array<{ value: T; label: string }>;
  onChange: (value: T) => void;
}) {
  const styles = useModelStyles();
  const t = useT();
  const selected = options.find((option) => option.value === value);
  return (
    <>
      <div className={styles.scenario} data-print="hide">
        <Text size={200} weight="semibold">
          {t('copilotAdoptionTimeSaved.scenarioLabel')}
        </Text>
        <RadioGroup
          layout="horizontal"
          value={value}
          onChange={(_e, data) => onChange(data.value as T)}
          aria-label={t('copilotAdoptionTimeSaved.scenarioLabel')}
        >
          {options.map((option) => (
            <Radio key={option.value} value={option.value} label={option.label} />
          ))}
        </RadioGroup>
      </div>
      {/* The radio buttons are not printed, but which cohort the table below is working for is the
          one thing a reader of the printout cannot otherwise tell. */}
      {selected && (
        <Text size={200} weight="semibold" className={styles.printedScenario} data-print="only">
          {t('copilotAdoptionTimeSaved.scenarioPrinted', { scenario: selected.label })}
        </Text>
      )}
    </>
  );
}

/**
 * What every calculator ends on: the hours in a day for the full-time restatement, a reset for every
 * figure, the reminder that two of the figures are shared with the other estimate, and where the
 * reader's figures are kept.
 */
export function CalculatorControls({ timeSaved }: { timeSaved: TimeSavedAssumptionState }) {
  const styles = useModelStyles();
  const t = useT();
  const { assumptions, defaults, customised, isCustomised, setAssumption, resetAssumption, resetAll } = timeSaved;

  return (
    <>
      <div className={styles.controlsRow}>
        <span className={styles.inline}>
          <Text size={200}>{t('copilotAdoptionTimeSaved.input.hoursPerDayLabel')}</Text>
          <AssumptionInput
            field="hoursPerDay"
            value={assumptions.hoursPerDay}
            defaultValue={defaults.hoursPerDay}
            customised={customised.includes('hoursPerDay')}
            label={t('copilotAdoptionTimeSaved.input.hoursPerDayLabel')}
            unit={t('copilotAdoptionTimeSaved.unit.hours')}
            onCommit={setAssumption}
            onReset={resetAssumption}
          />
        </span>
        {isCustomised && (
          <Button size="small" icon={<ArrowCounterclockwise16Regular />} onClick={resetAll} data-print="hide">
            {t('copilotAdoptionTimeSaved.input.resetAll')}
          </Button>
        )}
      </div>

      <Text size={100} className={styles.sub} style={{ marginTop: '8px' }}>
        {t('copilotAdoptionTimeSaved.input.sharedNote')}
      </Text>

      <MessageBar intent={isCustomised ? 'warning' : 'info'} style={{ marginTop: '12px' }}>
        <MessageBarBody>
          {isCustomised
            ? t('copilotAdoptionTimeSaved.storage.customised')
            : t('copilotAdoptionTimeSaved.storage.defaults')}
        </MessageBarBody>
      </MessageBar>
    </>
  );
}
