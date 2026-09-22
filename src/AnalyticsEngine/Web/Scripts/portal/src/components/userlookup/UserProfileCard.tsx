import { Fragment } from 'react';
import { Card, CardHeader, Subtitle2, Text, Badge, makeStyles, tokens } from '@fluentui/react-components';
import type { UserProfile } from '../../types/userData';
import { formatDateParts, formatNumber, useT, type TFunction } from '../../i18n';

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gridTemplateColumns: 'max-content 1fr',
    columnGap: '24px',
    rowGap: '6px',
    alignItems: 'baseline',
  },
  label: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  value: {
    wordBreak: 'break-word',
  },
  licenses: {
    marginTop: '16px',
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px',
  },
});

function yesNo(t: TFunction, value: boolean | null): string {
  if (value == null) return '—';
  return value ? t('admin.common.yes') : t('admin.common.no');
}

function formatUtc(value: string | null): string {
  if (!value) return '—';
  return formatDateParts(new Date(value), { dateStyle: 'short', timeStyle: 'medium', timeZone: 'UTC', timeZoneName: 'short' });
}

export default function UserProfileCard({ profile }: { profile: UserProfile }) {
  const styles = useStyles();
  const t = useT();
  const rows: Array<[string, string]> = [
    ['UPN', profile.userPrincipalName || '—'],
    ['Mail', profile.mail || '—'],
    [t('admin.userLookup.profile.azureAdId'), profile.azureAdId || '—'],
    [t('admin.userLookup.profile.accountEnabled'), yesNo(t, profile.accountEnabled)],
    [t('admin.userLookup.profile.department'), profile.department || '—'],
    [t('admin.userLookup.profile.jobTitle'), profile.jobTitle || '—'],
    [t('admin.userLookup.profile.company'), profile.companyName || '—'],
    [t('admin.userLookup.profile.office'), profile.officeLocation || '—'],
    [t('admin.userLookup.profile.countryOrRegion'), profile.countryOrRegion || '—'],
    [t('admin.userLookup.profile.usageLocation'), profile.usageLocation || '—'],
    [t('admin.userLookup.profile.stateOrProvince'), profile.stateOrProvince || '—'],
    [t('admin.userLookup.profile.postalCode'), profile.postalCode || '—'],
    [t('admin.userLookup.profile.manager'), profile.managerUserPrincipalName || '—'],
    [t('admin.userLookup.profile.lastUpdatedUtc'), formatUtc(profile.lastUpdatedUtc ?? profile.lastUpdated)],
  ];

  return (
    <Card>
      <CardHeader header={<Subtitle2>{t('admin.userLookup.profile.title')}</Subtitle2>} />
      <div className={styles.grid}>
        {rows.map(([label, value]) => (
          <Fragment key={label}>
            <Text className={styles.label}>{label}</Text>
            <Text className={styles.value}>{value}</Text>
          </Fragment>
        ))}
      </div>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
        {t('admin.userLookup.profile.utcContractWarning')}
      </Text>
      <div>
        <Text weight="semibold">{t('admin.userLookup.profile.licensesTitle', { count: formatNumber(profile.licenses.length) })}</Text>
        {profile.licenses.length > 0 ? (
          <div className={styles.licenses}>
            {profile.licenses.map((license, i) => (
              <Badge key={i} appearance="tint" color="brand">
                {license.name}
                {license.skuId ? ` (${license.skuId})` : ''}
              </Badge>
            ))}
          </div>
        ) : (
          <Text block style={{ color: tokens.colorNeutralForeground3 }}>
            {t('admin.userLookup.profile.noLicenses')}
          </Text>
        )}
      </div>
    </Card>
  );
}
