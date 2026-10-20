import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import type { UserProfile } from '../../types/userData';
import UserProfileCard from './UserProfileCard';

const profile: UserProfile = {
  userId: 1,
  userPrincipalName: 'user@contoso.example',
  mail: null,
  azureAdId: null,
  accountEnabled: true,
  lastUpdatedUtc: '2026-01-15T15:04:05+02:00',
  lastUpdated: null,
  department: null,
  jobTitle: null,
  companyName: null,
  countryOrRegion: null,
  officeLocation: null,
  usageLocation: null,
  stateOrProvince: null,
  postalCode: null,
  managerUserPrincipalName: null,
  licenses: [],
};

describe('UserProfileCard last updated timestamp', () => {
  it.each(['en', 'es'] as const)('renders the profile in %s with UTC date and time', (language) => {
    renderWithProvider(<UserProfileCard profile={profile} />, { language });

    expect(screen.getByText(profile.userPrincipalName)).toBeInTheDocument();
    expect(screen.getByText('15/01/2026, 13:04:05 UTC')).toBeInTheDocument();
  });

  it('uses the legacy timestamp when the UTC property is absent', () => {
    renderWithProvider(
      <UserProfileCard profile={{ ...profile, lastUpdatedUtc: null, lastUpdated: profile.lastUpdatedUtc }} />,
    );

    expect(screen.getByText('15/01/2026, 13:04:05 UTC')).toBeInTheDocument();
  });

  it('keeps the placeholder when neither timestamp is available', () => {
    renderWithProvider(<UserProfileCard profile={{ ...profile, lastUpdatedUtc: null }} />);

    const label = screen.getByText('Last updated (UTC)');
    expect(label.nextElementSibling).toHaveTextContent('\u2014');
  });
});
