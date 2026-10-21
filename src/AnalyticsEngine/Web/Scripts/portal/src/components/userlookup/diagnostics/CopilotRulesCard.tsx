import { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Subtitle2,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { ClassificationCatalogue } from '../../../types/userDiagnostics';
import { formatNumber, useT } from '../../../i18n';
import { classificationReason } from './diagnosticsText';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, wordBreak: 'break-all' },
  facts: { display: 'grid', gridTemplateColumns: 'minmax(200px, max-content) 1fr', columnGap: '24px', rowGap: '6px' },
  label: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2 },
  tableWrap: { overflowX: 'auto' },
  cellStack: { display: 'flex', flexDirection: 'column', gap: '2px' },
});

/**
 * The Copilot seat rules in force and how every licence in the tenant fares against them, with the rows where
 * Microsoft's licensing catalogue and the rules disagree flagged. Read-only: a what-if seat selection on the
 * Copilot Adoption report is how an administrator tests a different classification.
 */
export default function CopilotRulesCard({ catalogue }: { catalogue: ClassificationCatalogue }) {
  const styles = useStyles();
  const t = useT();
  const gaps = catalogue.tenantLicenceTypes.filter((type) => type.possibleRuleGap);
  const [open, setOpen] = useState(false);
  const rows = open ? catalogue.tenantLicenceTypes : gaps;

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('userDiagnostics.rules.title')}</Subtitle2>} />
      <div className={styles.stack}>
        <Text size={200} className={styles.muted}>
          {t('userDiagnostics.rules.description')}
        </Text>
        <Text size={200}>
          {t('userDiagnostics.rules.version', {
            version: catalogue.ruleSetVersion,
            build: catalogue.buildLabel ?? '\u2014',
            hash: catalogue.catalogueContentHash,
            count: formatNumber(catalogue.catalogueSkuCount),
          })}
        </Text>
        <div className={styles.facts}>
          <Text className={styles.label}>{t('userDiagnostics.rules.seatPrefixes')}</Text>
          <Text className={styles.mono}>{catalogue.seatSkuPrefixes.join(', ')}</Text>
          <Text className={styles.label}>{t('userDiagnostics.rules.excludedPrefixes')}</Text>
          <Text className={styles.mono}>{catalogue.excludedSkuPrefixes.join(', ')}</Text>
          <Text className={styles.label}>{t('userDiagnostics.rules.seatPlans')}</Text>
          <Text className={styles.mono}>{catalogue.copilotSeatServicePlans.join(', ')}</Text>
        </div>

        {rows.length > 0 && (
          <div className={styles.tableWrap}>
            <Table size="small" aria-label={t('userDiagnostics.rules.title')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnLicence')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.rules.columnAssigned')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnCopilot')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnWhy')}</TableHeaderCell>
                  <TableHeaderCell>{t('userDiagnostics.licences.columnCatalogue')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((type) => (
                  <TableRow key={type.id}>
                    <TableCell>
                      <div className={styles.cellStack}>
                        <Text>{type.name}</Text>
                        <Text className={styles.mono}>{type.skuPartNumber}</Text>
                      </div>
                    </TableCell>
                    <TableCell>{formatNumber(type.assignedUsers)}</TableCell>
                    <TableCell>
                      <Badge appearance="tint" color={type.classification.isCopilotSeat ? 'brand' : 'informative'}>
                        {type.classification.isCopilotSeat ? t('admin.common.yes') : t('admin.common.no')}
                      </Badge>
                    </TableCell>
                    <TableCell>
                      <Text size={200}>{classificationReason(t, type.classification)}</Text>
                    </TableCell>
                    <TableCell>
                      <div className={styles.cellStack}>
                        <Text size={200}>
                          {!type.catalogueListed
                            ? t('userDiagnostics.licences.catalogue.notListed')
                            : type.catalogueCarriesCopilotSeat
                              ? t('userDiagnostics.licences.catalogue.listedWithSeat')
                              : t('userDiagnostics.licences.catalogue.listed')}
                        </Text>
                        {type.possibleRuleGap && (
                          <Badge appearance="tint" color="warning">
                            {t('userDiagnostics.rules.gap')}
                          </Badge>
                        )}
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}

        <div>
          <Button appearance="subtle" onClick={() => setOpen((value) => !value)} aria-expanded={open}>
            {open
              ? t('userDiagnostics.rules.hide')
              : t('userDiagnostics.rules.show', { count: formatNumber(catalogue.tenantLicenceTypes.length) })}
          </Button>
        </div>
      </div>
    </Card>
  );
}
