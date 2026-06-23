import React from 'react';
import type { UserProfile } from '../../types/userData';

function yesNo(value: boolean | null): string {
  if (value == null) return '—';
  return value ? 'Yes' : 'No';
}

export default function UserProfileCard({ profile }: { profile: UserProfile }) {
  const rows: Array<[string, React.ReactNode]> = [
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
    <div className="card" style={{ marginBottom: '1.5rem' }}>
      <div className="card-header">
        <strong>Profile</strong>
      </div>
      <div className="card-body">
        <dl className="aa-profile-grid">
          {rows.map(([label, value]) => (
            <React.Fragment key={label}>
              <dt>{label}</dt>
              <dd>{value}</dd>
            </React.Fragment>
          ))}
        </dl>
        <div style={{ marginTop: '1rem' }}>
          <strong>Licenses ({profile.licenses.length})</strong>
          {profile.licenses.length > 0 ? (
            <ul style={{ marginBottom: 0 }}>
              {profile.licenses.map((license, i) => (
                <li key={i}>
                  {license.name}
                  {license.skuId ? ` (${license.skuId})` : ''}
                </li>
              ))}
            </ul>
          ) : (
            <div className="aa-muted">No licenses recorded.</div>
          )}
        </div>
      </div>
    </div>
  );
}
