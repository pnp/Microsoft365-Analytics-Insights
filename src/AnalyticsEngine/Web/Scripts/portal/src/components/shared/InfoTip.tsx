import {
  makeStyles,
  mergeClasses,
  tokens,
  Text,
  Button,
  Popover,
  PopoverSurface,
  PopoverTrigger,
} from '@fluentui/react-components';
import { Info16Regular } from '@fluentui/react-icons';
import type { ReactNode } from 'react';
import { useT } from '../../i18n';

const useStyles = makeStyles({
  trigger: {
    minWidth: '20px',
    width: '20px',
    height: '20px',
    padding: 0,
    color: tokens.colorNeutralForeground3,
  },
  // Wide enough for a multi-line calculation to sit on one line each: a formula that wraps
  // mid-expression reads as a different formula, which defeats the point of showing it verbatim.
  surface: {
    maxWidth: 'min(560px, calc(100vw - 32px))',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  title: {
    color: tokens.colorNeutralForeground1,
  },
  body: {
    color: tokens.colorNeutralForeground2,
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  // "how" is prose in most tips but a short list in the ones explaining a composite score, where a
  // single paragraph is a wall of text nobody reads. Fluent's Text renders a <span>, which cannot
  // legally contain a list, so this is a plain div carrying the same typography.
  how: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    '& p': {
      margin: 0,
    },
    '& ul': {
      margin: 0,
      paddingLeft: '18px',
      display: 'flex',
      flexDirection: 'column',
      gap: '4px',
    },
  },
  formulaLabel: {
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  formula: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: '12px',
    lineHeight: '18px',
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusSmall,
    padding: '6px 8px',
    whiteSpace: 'pre-wrap',
    color: tokens.colorNeutralForeground1,
  },
  // A multi-line calculation is aligned on purpose, so it must never be re-wrapped: scroll it
  // instead. Single-line formulas are often a sentence of arithmetic and still wrap normally.
  formulaBlock: {
    whiteSpace: 'pre',
    overflowX: 'auto',
  },
  source: {
    color: tokens.colorNeutralForeground3,
  },
});

/** What an "i" button has to be able to answer for any figure on the page. */
export type InfoTipContent = {
  /** What the number claims, in one sentence. */
  what: ReactNode;
  /** How it is worked out, in words - a short list where the figure has several components. */
  how?: ReactNode;
  /** The calculation itself, shown verbatim so it can be checked rather than trusted. */
  formula?: string;
  /** Which import the underlying data came from, and any caveat that goes with it. */
  source?: ReactNode;
};

/**
 * The small "i" that sits on any figure or chart making an assertion.
 *
 * Every number on this page ends up in a licence negotiation or a conversation with a department
 * lead, and the first two questions asked about all of them are "what exactly does that mean?" and
 * "how do you know?". Answering those in a methodology tab alone is not enough - by the time the
 * reader has a question they are looking at the number, not at the tab - so the explanation is
 * attached to the figure itself, with the actual formula in it rather than a paraphrase.
 */
export default function InfoTip({ title, content }: { title: string; content: InfoTipContent }) {
  const styles = useStyles();
  const t = useT();
  const formulaIsMultiLine = typeof content.formula === 'string' && content.formula.includes('\n');
  const explainLabel = t('common.infoTip.ariaLabel', { title });

  return (
    <Popover withArrow positioning="below-end">
      <PopoverTrigger disableButtonEnhancement>
        <Button
          appearance="transparent"
          className={styles.trigger}
          icon={<Info16Regular />}
          aria-label={explainLabel}
          title={explainLabel}
          data-print="hide"
        />
      </PopoverTrigger>
      <PopoverSurface className={styles.surface}>
        <Text weight="semibold" size={300} className={styles.title}>
          {title}
        </Text>
        <div className={styles.body}>
          <Text size={200}>{content.what}</Text>
          {content.how && <div className={styles.how}>{content.how}</div>}
          {content.formula && (
            <div>
              <Text size={100} block className={styles.formulaLabel}>
                {t('common.infoTip.calculation')}
              </Text>
              <div
                className={mergeClasses(styles.formula, formulaIsMultiLine && styles.formulaBlock)}
              >
                {content.formula}
              </div>
            </div>
          )}
          {content.source && (
            <Text size={100} className={styles.source}>
              {content.source}
            </Text>
          )}
        </div>
      </PopoverSurface>
    </Popover>
  );
}
