import { Text, makeStyles, tokens } from '@fluentui/react-components';
import { useT } from '../../i18n';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  summary: {
    cursor: 'pointer',
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    marginTop: '6px',
  },
  example: {
    margin: 0,
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground3,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase300,
    whiteSpace: 'pre',
    overflowX: 'auto',
  },
  rules: {
    margin: 0,
    paddingInlineStart: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    color: tokens.colorNeutralForeground2,
  },
});

/** A header cell as it would be written in the file, quoted if the name holds a separator or a quote. */
function csvField(value: string): string {
  return /[",;\t|\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

export interface CsvFileFormatProps {
  /**
   * The organisation type's name, used as the value column's header so the example is the file the
   * administrator is about to make. Falls back to a generic header while the name is still empty.
   */
  typeName?: string | null;
  /** Folded behind a "what the file looks like" disclosure, for a card someone returns to. */
  collapsed?: boolean;
}

/**
 * What an organisation file has to look like, with an example to copy.
 *
 * "A user column and an organisation column" leaves an administrator guessing at the header, the
 * separator and the encoding, and the guess that goes wrong is the expensive one: a file read with
 * the wrong separator matches nobody, and in a Replace import nobody matched means everybody's
 * value is cleared. So the rules the parser actually applies are spelled out beside an example.
 */
export default function CsvFileFormat({ typeName, collapsed = false }: CsvFileFormatProps) {
  const styles = useStyles();
  const t = useT();
  const column = csvField(typeName?.trim() || t('userOrgs.csvFormat.defaultColumn'));

  const body = (
    <div className={styles.body}>
      <Text size={200}>{t('userOrgs.csvFormat.intro')}</Text>
      <pre className={styles.example} aria-label={t('userOrgs.csvFormat.exampleAria')}>
        {t('userOrgs.csvFormat.example', { column })}
      </pre>
      <ul className={styles.rules}>
        <li>
          <Text size={200}>{t('userOrgs.csvFormat.ruleHeader')}</Text>
        </li>
        <li>
          <Text size={200}>{t('userOrgs.csvFormat.ruleSeparator')}</Text>
        </li>
        <li>
          <Text size={200}>{t('userOrgs.csvFormat.ruleBlank')}</Text>
        </li>
        <li>
          <Text size={200}>{t('userOrgs.csvFormat.ruleUsers')}</Text>
        </li>
      </ul>
    </div>
  );

  return collapsed ? (
    <details className={styles.root}>
      <summary className={styles.summary}>{t('userOrgs.csvFormat.title')}</summary>
      {body}
    </details>
  ) : (
    <div className={styles.root}>
      <Text weight="semibold" size={300}>
        {t('userOrgs.csvFormat.title')}
      </Text>
      {body}
    </div>
  );
}
