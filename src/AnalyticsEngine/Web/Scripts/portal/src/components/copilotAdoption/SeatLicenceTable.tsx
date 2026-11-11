import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { LicenceTypeClassification, SeatIdleWithheldReason } from '../../types/copilotAdoption';
import { formatCount } from '../shared/KpiGrid';
import { useT, type TFunction, type TranslationKey } from '../../i18n';

const useStyles = makeStyles({
  skuTable: {
    width: '100%',
    borderCollapse: 'collapse',
  },
  skuCell: {
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
    textAlign: 'left',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

/** The server's floor for a reader without See PII (`ReportScopeResolver.MinimumPeopleWithoutSeePii`), when a row does not carry it. */
const WITHHELD_FLOOR = 5;

/** Why a withheld idle count is not shown; a reason this build does not know gets the general sentence. */
function withheldIdleKey(reason: SeatIdleWithheldReason | null | undefined): TranslationKey {
  switch (reason) {
    case 'fewHolders':
      return 'copilotAdoption.page.skuIdle.withheld.fewHolders';
    case 'revealsSmallerSeat':
      return 'copilotAdoption.page.skuIdle.withheld.revealsSmallerSeat';
    default:
      return 'copilotAdoption.page.skuIdle.withheld.other';
  }
}

function withheldIdleText(t: TFunction, licence: LicenceTypeClassification): string {
  return t(withheldIdleKey(licence.assignedIdleWithheldReason), { count: formatCount(licence.minimumHolders ?? WITHHELD_FLOOR) });
}

/**
 * Every licence type the tool found, and whether it counted it as a Copilot seat, so the licensed population can be
 * checked rather than taken on trust.
 *
 * A reader without See PII is not given the idle count of a Copilot seat that a handful of people hold, nor of the seat
 * that, with the reclaimable total, would give it back (#844): the cell says why, never 0. SKU names are tenant data and
 * render as stored.
 */
export default function SeatLicenceTable({ licences }: { licences: LicenceTypeClassification[] }) {
  const styles = useStyles();
  const t = useT();
  const withheld = licences.find((l) => l.assignedIdleWithheld);
  const complement = licences.some((l) => l.assignedIdleWithheld && l.assignedIdleWithheldReason === 'revealsSmallerSeat');

  return (
    <>
      <table className={styles.skuTable}>
        <thead>
          <tr>
            <th className={styles.skuCell}>{t('copilotAdoption.page.product')}</th>
            <th className={styles.skuCell}>SKU</th>
            <th className={styles.skuCell}>{t('copilotAdoption.page.assigned')}</th>
            <th className={styles.skuCell}>{t('copilotAdoption.page.purchased')}</th>
            <th className={styles.skuCell}>{t('copilotAdoption.page.unassigned')}</th>
            <th className={styles.skuCell}>{t('copilotAdoption.page.assignedIdle')}</th>
            <th className={styles.skuCell}>{t('copilotAdoption.page.countedCopilotLicence')}</th>
          </tr>
        </thead>
        <tbody>
          {licences.map((licence) => (
            <tr key={licence.id}>
              <td className={styles.skuCell}>{licence.name}</td>
              <td className={styles.skuCell}>
                <Text size={200} className={styles.muted}>
                  {licence.skuPartNumber}
                </Text>
              </td>
              <td className={styles.skuCell}>{formatCount(licence.assignedUsers)}</td>
              <td className={styles.skuCell}>{licence.purchasedUnits == null ? t('copilotAdoption.page.unknown') : formatCount(licence.purchasedUnits)}</td>
              <td className={styles.skuCell}>{licence.unassignedUnits == null ? t('copilotAdoption.page.unknown2') : formatCount(licence.unassignedUnits)}</td>
              <td className={styles.skuCell}>
                {licence.assignedIdleWithheld ? (
                  <Text size={200} className={styles.muted}>
                    {withheldIdleText(t, licence)}
                  </Text>
                ) : licence.assignedIdleUsers == null ? (
                  '\u2014'
                ) : (
                  formatCount(licence.assignedIdleUsers)
                )}
              </td>
              <td className={styles.skuCell}>{licence.isCopilotSeat ? t('copilotAdoption.page.yes') : t('copilotAdoption.page.no')}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {withheld && (
        <Text size={200} className={styles.muted}>
          {t('copilotAdoption.page.skuIdle.withheldNote', { count: formatCount(withheld.minimumHolders ?? WITHHELD_FLOOR) })}
          {complement && (
            <>
              {' '}
              {t('copilotAdoption.page.skuIdle.withheldNoteComplement')}
            </>
          )}
        </Text>
      )}
    </>
  );
}
