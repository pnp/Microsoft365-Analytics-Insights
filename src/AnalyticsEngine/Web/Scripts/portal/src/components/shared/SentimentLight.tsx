import { makeStyles, tokens, Text } from '@fluentui/react-components';
import { formatNumber, translateActive, useT, type TFunction, type TranslationKey } from '../../i18n';

/**
 * Sentiment, drawn as a traffic light.
 *
 * The score used to be printed as "0.43 (leaning negative)". That is precise and almost unreadable
 * at a glance: scanning a leaderboard of twenty channels, a reader has to parse twenty two-decimal
 * numbers on a scale they have to remember is NOT a percentage. A traffic light answers the actual
 * question - is this channel fine, watch it, or look at it - in one glance, and the exact figure is
 * still one hover away for anyone who needs to quote it.
 *
 * IMPORTANT: the stored score is a message-count weighted mean of a TERNARY score (negative = 0,
 * neutral = 0.5, positive = 1), so 0.5 is exactly neutral and the number is NOT a percentage of
 * positive messages. It is also a different scale from `copilot_interactions.sentiment_score`, which
 * stores the raw positive-confidence score, so the two must never be compared. Everything that
 * renders sentiment goes through here so that distinction can only be made in one place.
 */

/** The three lamps, coldest first, matching how the eye reads a traffic light. */
type Lamp = 'negative' | 'neutral' | 'positive';

const LAMP_ORDER: Lamp[] = ['negative', 'neutral', 'positive'];

const LAMP_COLOUR: Record<Lamp, string> = {
  // Deliberately the same palette the adoption bands use, so "red" means the same thing on every
  // page of the portal.
  negative: '#d13438',
  neutral: '#c19c00',
  positive: '#107c10',
};

/** The word for a sentiment score, using bands either side of neutral. */
type SentimentBand = 'negative' | 'leaning negative' | 'neutral' | 'leaning positive' | 'positive';

/** The word for a sentiment score, using bands either side of neutral. */
export function sentimentLabel(value: number): SentimentBand {
  if (value < 0.35) return 'negative';
  if (value < 0.45) return 'leaning negative';
  if (value <= 0.55) return 'neutral';
  if (value <= 0.65) return 'leaning positive';
  return 'positive';
}

const SENTIMENT_LABEL_KEYS: Record<SentimentBand, TranslationKey> = {
  negative: 'common.sentiment.band.negative',
  'leaning negative': 'common.sentiment.band.leaningNegative',
  neutral: 'common.sentiment.band.neutral',
  'leaning positive': 'common.sentiment.band.leaningPositive',
  positive: 'common.sentiment.band.positive',
};

/**
 * Which lamp is lit.
 *
 * The five written bands collapse onto three lamps, because a traffic light with five lamps is not a
 * traffic light. The nuance is not lost - "leaning negative" is still spelled out beside the lamps
 * and in the hover text - but the lamp answers the coarse question the colour is for.
 */
export function sentimentLamp(value: number): Lamp {
  if (value < 0.45) return 'negative';
  return value <= 0.55 ? 'neutral' : 'positive';
}

/**
 * Sentiment in words and figures, for CSV exports, tooltips and anywhere a graphic will not do.
 *
 * Kept as text rather than a percentage on purpose: see the note on the scale above.
 */
export function formatSentiment(value: number | null | undefined, t?: TFunction): string {
  if (value === null || value === undefined) return '\u2014';
  return `${value.toFixed(2)} (${(t ?? translateActive)(SENTIMENT_LABEL_KEYS[sentimentLabel(value)])})`;
}

/** Explains the sentiment scale wherever it is shown. */
export function sentimentScaleNote(t: TFunction = translateActive): string {
  return t('common.sentiment.scaleNote');
}

const useStyles = makeStyles({
  root: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    whiteSpace: 'nowrap',
    cursor: 'help',
  },
  lamps: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '3px',
    padding: '2px 3px',
    borderRadius: '999px',
    backgroundColor: tokens.colorNeutralBackground3,
    borderTopWidth: '1px',
    borderRightWidth: '1px',
    borderBottomWidth: '1px',
    borderLeftWidth: '1px',
    borderTopStyle: 'solid',
    borderRightStyle: 'solid',
    borderBottomStyle: 'solid',
    borderLeftStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke2,
    borderRightColor: tokens.colorNeutralStroke2,
    borderBottomColor: tokens.colorNeutralStroke2,
    borderLeftColor: tokens.colorNeutralStroke2,
  },
  lamp: {
    width: '9px',
    height: '9px',
    borderRadius: '50%',
  },
  /**
   * An unlit lamp is drawn, not hidden. Three positions with one filled is what makes the graphic
   * read as a traffic light rather than as an arbitrary coloured dot, and it shows at a glance which
   * way a channel sits on the scale.
   */
  lampOff: {
    backgroundColor: tokens.colorNeutralStroke2,
  },
  label: {
    color: tokens.colorNeutralForeground2,
  },
  unscored: {
    color: tokens.colorNeutralForeground3,
  },
});

/**
 * @param value The stored sentiment score, 0-1, or null/undefined when the period was never scored.
 * @param showLabel Whether to spell the band out beside the lamps. Off in dense contexts where the
 *   colour alone is the point and the hover carries the detail.
 */
export default function SentimentLight({
  value,
  showLabel = true,
}: {
  value: number | null | undefined;
  showLabel?: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const scaleNote = t('common.sentiment.scaleNote');

  // Zero is the most NEGATIVE possible score, so an unscored period must never be drawn as a lit red
  // lamp - that would invert the meaning of "we have no data".
  if (value === null || value === undefined) {
    return (
      <Text
        size={200}
        className={styles.unscored}
        title={t('common.sentiment.notScored', { note: scaleNote })}
      >
        {'\u2014'}
      </Text>
    );
  }

  const lit = sentimentLamp(value);
  const label = sentimentLabel(value);
  const translatedLabel = t(SENTIMENT_LABEL_KEYS[label]);
  const detail = t('common.sentiment.detail', {
    score: formatNumber(value, { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
    band: translatedLabel,
    note: scaleNote,
  });

  return (
    <span className={styles.root} title={detail}>
      <span className={styles.lamps} role="img" aria-label={detail}>
        {LAMP_ORDER.map((lamp) => (
          <span
            key={lamp}
            data-lamp={lamp}
            data-lit={lamp === lit ? 'true' : 'false'}
            className={`${styles.lamp}${lamp === lit ? '' : ` ${styles.lampOff}`}`}
            style={lamp === lit ? { backgroundColor: LAMP_COLOUR[lamp] } : undefined}
          />
        ))}
      </span>
      {showLabel && (
        <Text size={200} className={styles.label}>
          {translatedLabel}
        </Text>
      )}
    </span>
  );
}
