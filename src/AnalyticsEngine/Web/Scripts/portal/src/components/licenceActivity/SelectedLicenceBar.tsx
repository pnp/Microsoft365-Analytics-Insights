import { memo, useMemo } from 'react';
import { makeStyles, tokens, Card, Text, Select } from '@fluentui/react-components';
import type { LicenceActivitySku } from '../../types/licenceActivity';
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

  const sorted = useMemo(
    () =>
      [...licences].sort(
        (a, b) => b.assignedUsers - a.assignedUsers || licenceName(a).localeCompare(licenceName(b)),
      ),
    [licences],
  );

  const selected = licences.find((s) => s.licenceTypeId === selectedLicenceTypeId) ?? null;

  if (licences.length === 0) return null;

  return (
    <Card className={styles.card}>
      <Text size={200} weight="semibold" className={styles.label}>
        Licence
      </Text>
      <Select
        className={styles.select}
        value={selectedLicenceTypeId == null ? '' : String(selectedLicenceTypeId)}
        aria-label="Selected licence"
        onChange={(_e, d) => {
          if (d.value !== '') onSelect(Number(d.value));
        }}
      >
        {selectedLicenceTypeId == null && <option value="">Choose a licence</option>}
        {sorted.map((sku) => (
          <option key={sku.licenceTypeId} value={sku.licenceTypeId}>
            {licenceName(sku)}
          </option>
        ))}
      </Select>
      {selected && (
        <Text size={200} className={styles.caption}>
          {formatCount(selected.assignedUsers)} people hold this licence
        </Text>
      )}
    </Card>
  );
}

// Memoised: renders only when the licence list or the selection changes, not on every page re-render.
export default memo(SelectedLicenceBar);
