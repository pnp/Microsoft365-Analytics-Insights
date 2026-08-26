import {
  makeStyles,
  tokens,
  Text,
  Button,
  Popover,
  PopoverSurface,
  PopoverTrigger,
} from '@fluentui/react-components';
import { Info16Regular } from '@fluentui/react-icons';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  trigger: {
    minWidth: '20px',
    width: '20px',
    height: '20px',
    padding: 0,
    color: tokens.colorNeutralForeground3,
  },
  surface: {
    maxWidth: '380px',
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
  source: {
    color: tokens.colorNeutralForeground3,
  },
});

/** What an "i" button has to be able to answer for any figure on the page. */
export type InfoTipContent = {
  /** What the number claims, in one sentence. */
  what: ReactNode;
  /** How it is worked out, in words. */
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

  return (
    <Popover withArrow positioning="below-end">
      <PopoverTrigger disableButtonEnhancement>
        <Button
          appearance="transparent"
          className={styles.trigger}
          icon={<Info16Regular />}
          aria-label={`How "${title}" is calculated`}
          title={`How "${title}" is calculated`}
        />
      </PopoverTrigger>
      <PopoverSurface className={styles.surface}>
        <Text weight="semibold" size={300} className={styles.title}>
          {title}
        </Text>
        <div className={styles.body}>
          <Text size={200}>{content.what}</Text>
          {content.how && <Text size={200}>{content.how}</Text>}
          {content.formula && (
            <div>
              <Text size={100} block className={styles.formulaLabel}>
                Calculation
              </Text>
              <div className={styles.formula}>{content.formula}</div>
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
