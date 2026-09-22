import React from 'react';
import { Title3, Subtitle1, Text, MessageBar, MessageBarBody } from '@fluentui/react-components';
import TeamList from '../components/teams/TeamList';
import Spinner from '../components/Spinner';
import { GraphResponse } from '../types/GraphResponse';
import { fetchMsGraph, GRAPH_ENDPOINTS } from '../auth/graph';
import { fetchGraphToken } from '../auth/siteToken';
import type { GraphAccessToken } from '../types/graphToken';
import type { User, Team } from '@microsoft/microsoft-graph-types';
import { useT, type TFunction } from '../i18n';

type TeamsPermissionsProps = {
  t: TFunction;
};

type TeamsPermissionsState = {
  loading: boolean;
  error: string | null;
  /** Set when the site couldn't mint a Graph token for this session - nothing can load without one. */
  noToken: boolean;
  joinedTeams: Array<Team> | null;
  graphProfile: User | null;
};

/**
 * Authorise / de-authorise Teams for deep analytics.
 *
 * Auth is entirely server-side: the user has already signed in to the site via the server's OIDC
 * redirect, and `POST api/SiteTokenAPI` mints a fresh Graph access token from the refresh token
 * held in the auth cookie. The browser then calls Graph directly with it.
 *
 * There used to be a second, client-side MSAL sign-in path for when that call failed. It was
 * removed: it was pinned to a hard-coded app registration that no longer resolves, so it could
 * not sign anyone in - it only replaced a clear failure with an opaque one.
 *
 * Tokens are fetched at the point of use rather than cached on the page, because a Graph access
 * token only lasts about an hour and this page is one an admin leaves open.
 */
class TeamsPermissionsPageInner extends React.Component<TeamsPermissionsProps, TeamsPermissionsState> {
  constructor(props: TeamsPermissionsProps) {
    super(props);
    this.state = {
      loading: true,
      error: null,
      noToken: false,
      joinedTeams: null,
      graphProfile: null,
    };
  }

  async loadTeamsData(tokenResponse: GraphAccessToken) {
    // Get profile
    const graphProfile: User = await fetchMsGraph(GRAPH_ENDPOINTS.ME, tokenResponse.accessToken).catch(() => {
      this.setState({ error: this.props.t('admin.teamsPermissions.errors.fetchGraphProfile') });
    });

    if (graphProfile) {
      this.setState({ graphProfile });
    }

    // Get teams
    return this.getJoinedTeams(tokenResponse.accessToken);
  }

  // React events
  async componentDidMount() {
    // Ask the site for a Graph token for the signed-in admin.
    const serverSideToken = await fetchGraphToken();

    if (!serverSideToken) {
      this.setState({ loading: false, noToken: true });
      return;
    }

    return this.loadTeamsData(serverSideToken);
  }

  async getJoinedTeams(accessToken: string) {
    console.log('Loading teams for user from Graph');
    const joinedTeamsResponse: GraphResponse<Team> = await fetchMsGraph(
      GRAPH_ENDPOINTS.JOINED_TEAMS,
      accessToken,
    ).catch(() => {
      this.setState({ error: this.props.t('admin.teamsPermissions.errors.fetchJoinedTeams') });
    });

    if (joinedTeamsResponse) {
      this.setState({
        joinedTeams: joinedTeamsResponse.value,
        error: null,
      });
    }

    this.setState({ loading: false });
  }

  render() {
    const t = this.props.t;
    return (
      <div>
        <Title3 block>{t('admin.teamsPermissions.title')}</Title3>
        <div style={{ height: 16 }} />
        {this.state.loading ? (
          <div style={{ textAlign: 'center', padding: '32px' }}>
            <Spinner size={100} label={t('admin.teamsPermissions.loadingTeams')} />
          </div>
        ) : (
          <div>
            {this.state.noToken && (
              <MessageBar intent="error" style={{ marginBlock: '12px' }}>
                <MessageBarBody>
                  {t('admin.teamsPermissions.noTokenMessage')}
                </MessageBarBody>
              </MessageBar>
            )}

            {this.state.error && (
              <MessageBar intent="error" style={{ marginBlock: '12px' }}>
                <MessageBarBody>{this.state.error}</MessageBarBody>
              </MessageBar>
            )}

            <Text block style={{ marginBlock: '12px' }}>
              {t('admin.teamsPermissions.description')}
            </Text>

            <section>
              {this.state.joinedTeams ? (
                <div>
                  <Subtitle1 block style={{ marginBlock: '12px' }}>
                    {t('admin.teamsPermissions.yourTeamsTitle', {
                      displayName: this.state.graphProfile?.displayName ?? '',
                    })}
                  </Subtitle1>
                  <Text block style={{ marginBottom: '8px' }}>
                    {t('admin.teamsPermissions.yourTeamsDescription')}
                  </Text>
                  <TeamList teamsList={this.state.joinedTeams} />
                </div>
              ) : (
                <Text block>
                  {this.state.noToken
                    ? t('admin.teamsPermissions.noTokenTeamsPlaceholder')
                    : t('admin.teamsPermissions.noTeamsFound')}
                </Text>
              )}
            </section>
            <Text block size={200} style={{ marginTop: '12px', color: 'var(--colorNeutralForeground3)' }}>
              {t('admin.teamsPermissions.tokenNote')}
            </Text>
          </div>
        )}
      </div>
    );
  }
}

export default function TeamsPermissionsPage() {
  const t = useT();
  return <TeamsPermissionsPageInner t={t} />;
}
