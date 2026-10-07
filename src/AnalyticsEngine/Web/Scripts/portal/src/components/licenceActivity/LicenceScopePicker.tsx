import { memo, useId, useMemo } from 'react';
import { makeStyles, tokens, Label, Select } from '@fluentui/react-components';
import type { LicenceActivitySku } from '../../types/licenceActivity';
import { compareStrings, useT } from '../../i18n';
import { formatCount, licenceName } from './format';
import { ALL_LICENCES, type LicenceScope } from './scope';

const useStyles = makeStyles({
  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  label: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  select: {
    minWidth: '280px',
    maxWidth: '420px',
  },
});

interface LicenceScopePickerProps {
  licences: LicenceActivitySku[];
  /** How many people hold any licence, for the "everyone" option. */
  allAssignedUsers: number;
  value: LicenceScope;
  onChange: (scope: LicenceScope) => void;
  /** The visible label above the drop-down. Also its accessible name. */
  label: string;
  /** Leave this scope out of the options - used by "compare with" so a licence is never compared with itself. */
  exclude?: LicenceScope;
}

/**
 * Picks the population a view describes: everyone holding a licence (first, and the default) or one
 * licence. Licences are listed by name - the list can be long, and a name is what a reader looks for -
 * with how many people hold each. Licences nobody holds are left out: there is nothing to show for them.
 */
function LicenceScopePicker({ licences, allAssignedUsers, value, onChange, label, exclude }: LicenceScopePickerProps) {
  const styles = useStyles();
  const t = useT();
  const id = useId();

  const options = useMemo(
    () =>
      licences
        .filter((l) => (l.assignedUsers > 0 || l.licenceTypeId === value) && l.licenceTypeId !== exclude)
        .sort((a, b) => compareStrings(licenceName(a), licenceName(b)) || a.licenceTypeId - b.licenceTypeId),
    [licences, value, exclude],
  );

  return (
    <div className={styles.field}>
      <Label htmlFor={id} size="small" weight="semibold" className={styles.label}>
        {label}
      </Label>
      <Select
        id={id}
        className={styles.select}
        value={String(value)}
        onChange={(_e, d) => onChange(d.value === ALL_LICENCES ? ALL_LICENCES : Number(d.value))}
      >
        {exclude !== ALL_LICENCES && (
          <option value={ALL_LICENCES}>
            {t('licenceActivity.scope.optionWithCount', {
              name: t('licenceActivity.scope.allLicensedUsers'),
              count: formatCount(allAssignedUsers),
            })}
          </option>
        )}
        {options.map((sku) => (
          <option key={sku.licenceTypeId} value={sku.licenceTypeId}>
            {t('licenceActivity.scope.optionWithCount', { name: licenceName(sku), count: formatCount(sku.assignedUsers) })}
          </option>
        ))}
      </Select>
    </div>
  );
}

export default memo(LicenceScopePicker);
