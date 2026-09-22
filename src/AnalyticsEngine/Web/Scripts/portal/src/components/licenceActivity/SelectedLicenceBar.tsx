import { memo, useMemo } from 'react';
import { makeStyles, tokens, Card, Text, Select } from '@fluentui/react-components';
import type { LicenceActivitySku } from '../../types/licenceActivity';
import { compareStrings, useT } from '../../i18n';
import { formatCount, licenceName } from './format';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    padding: '10px 14px',
  },
  label: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  select: {
    minWidth: '260px',
  },
  caption: {
    color: tokens.colorNeutralForeground3,
  },
});

interface SelectedLicenceBarProps {
  licences: LicenceActivitySku[];
  selectedLicenceTypeId: number | null;
  onSelect: (licenceTypeId: number) => void;
}

/**
 * The persistent selected-licence context for the tabs that are scoped to one licence (By service,
 * People). The Overview tab has the full assignments table to pick from; away from it, this keeps the
 * chosen licence visible and switchable without going back, so the reader never loses their place in
 * the tab they are reading.
 *
 * Licences are listed biggest-first (by assigned users), matching the assignments table's default
 * order, so the same licence sits in the same relative place in both.
 */
function SelectedLicenceBar({ licences, selectedLicenceTypeId, onSelect }: SelectedLicenceBarProps) {
  const styles = useStyles();
  const t = useT();

  const sorted = useMemo(
    () =>
      [...licences].sort(
        (a, b) => b.assignedUsers - a.assignedUsers || compareStrings(licenceName(a), licenceName(b)),
      ),
    [licences],
  );

  const selected = licences.find((s) => s.licenceTypeId === selectedLicenceTypeId) ?? null;

  if (licences.length === 0) return null;

  return (
    <Card className={styles.card}>
      <Text size={200} weight="semibold" className={styles.label}>
        {t('licenceActivity.common.licence')}
      </Text>
      <Select
        className={styles.select}
        value={selectedLicenceTypeId == null ? '' : String(selectedLicenceTypeId)}
        aria-label={t('licenceActivity.selectedLicence.aria')}
        onChange={(_e: any, d: any) => {
          if (d.value !== '') onSelect(Number(d.value));
        }}
      >
        {selectedLicenceTypeId == null && <option value="">{t('licenceActivity.selectedLicence.choose')}</option>}
        {sorted.map((sku) => (
          <option key={sku.licenceTypeId} value={sku.licenceTypeId}>
            {licenceName(sku)}
          </option>
        ))}
      </Select>
      {selected && (
        <Text size={200} className={styles.caption}>
          {t('licenceActivity.selectedLicence.peopleHold', { count: formatCount(selected.assignedUsers) })}
        </Text>
      )}
    </Card>
  );
}

// Memoised: renders only when the licence list or the selection changes, not on every page re-render.
export default memo(SelectedLicenceBar);
