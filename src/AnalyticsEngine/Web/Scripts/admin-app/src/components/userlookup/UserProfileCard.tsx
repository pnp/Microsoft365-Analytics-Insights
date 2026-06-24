import { Fragment } from 'react';
import { Card, CardHeader, Subtitle2, Text, Badge, makeStyles, tokens } from '@fluentui/react-components';
import type { UserProfile } from '../../types/userData';

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

function yesNo(value: boolean | null): string {
  if (value == null) return '—';
  return value ? 'Yes' : 'No';
}

export default function UserProfileCard({ profile }: { profile: UserProfile }) {
  const styles = useStyles();
  const rows: Array<[string, string]> = [
    ['UPN', profile.userPrincipalName || '—'],
    ['Mail', profile.mail || '—'],
    ['Azure AD id', profile.azureAdId || '—'],
    ['Account enabled', yesNo(profile.accountEnabled)],
    ['Department', profile.department || '—'],
    ['Job title', profile.jobTitle || '—'],
    ['Company', profile.companyName || '—'],
    ['Office', profile.officeLocation || '—'],
    ['Country / region', profile.countryOrRegion || '—'],
    ['Usage location', profile.usageLocation || '—'],
    ['State / province', profile.stateOrProvince || '—'],
    ['Postal code', profile.postalCode || '—'],
    ['Manager', profile.managerUserPrincipalName || '—'],
    ['Last updated', profile.lastUpdated ? new Date(profile.lastUpdated).toLocaleString() : '—'],
  ];

  return (
    <Card>
      <CardHeader header={<Subtitle2>Profile</Subtitle2>} />
      <div className={styles.grid}>
        {rows.map(([label, value]) => (
          <Fragment key={label}>
            <Text className={styles.label}>{label}</Text>
            <Text className={styles.value}>{value}</Text>
          </Fragment>
        ))}
      </div>
      <div>
        <Text weight="semibold">Licenses ({profile.licenses.length})</Text>
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
            No licenses recorded.
          </Text>
        )}
      </div>
    </Card>
  );
}
