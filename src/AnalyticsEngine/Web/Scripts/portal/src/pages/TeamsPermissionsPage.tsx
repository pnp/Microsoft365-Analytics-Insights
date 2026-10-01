import React, { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  Title3,
  Subtitle1,
  Text,
  Button,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
} from '@fluentui/react-components';
import TeamList from '../components/teams/TeamList';
import Spinner from '../components/Spinner';
import { GraphResponse } from '../types/GraphResponse';
import { fetchMsGraph, GRAPH_ENDPOINTS } from '../auth/graph';
import { fetchGraphToken } from '../auth/siteToken';
import {
  TEAMS_CONNECT_URL,
  readTeamsConnectOutcome,
  withoutTeamsConnectOutcome,
  type TeamsConnectOutcome,
} from '../auth/teamsConnect';
import type { GraphAccessToken } from '../types/graphToken';
import type { User, Team } from '@microsoft/microsoft-graph-types';
import { useT, type TFunction } from '../i18n';

type TeamsPermissionsProps = {
  t: TFunction;
  /** Why the Teams connection the admin has just come back from failed, if it did. */
  connectOutcome: TeamsConnectOutcome | null;
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
 * Signing in doesn't capture that refresh token any more (issue #670): it needs the delegated
 * Teams permissions, and a tenant that hasn't granted them - often because it doesn't use this page
 * at all - used to be locked out of the whole portal. So when the site has no token, this page
 * offers "Connect to Microsoft Teams" instead (`src/auth/teamsConnect.ts`), which asks Entra ID for
 * those permissions and comes back here. If Entra ID says no, the server says why in the query string.
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
    const connectOutcome = this.props.connectOutcome;
    return (
      <div>
        <Title3 block>{t('admin.teamsPermissions.title')}</Title3>
        <div style={{ height: 16 }} />
        {connectOutcome && (
          <MessageBar intent="error" layout="multiline" style={{ marginBlock: '12px' }}>
            <MessageBarBody>
              <MessageBarTitle>{t('admin.teamsPermissions.connect.outcomeTitle')}</MessageBarTitle>
              {t(connectOutcome.messageKey)}
              {connectOutcome.errorCode && (
                <Text block size={200} style={{ marginTop: '4px' }}>
                  {t('admin.teamsPermissions.connect.errorCode', { code: connectOutcome.errorCode })}
                </Text>
              )}
            </MessageBarBody>
          </MessageBar>
        )}
        {this.state.loading ? (
          <div style={{ textAlign: 'center', padding: '32px' }}>
            <Spinner size={100} label={t('admin.teamsPermissions.loadingTeams')} />
          </div>
        ) : (
          <div>
            {this.state.noToken && (
              <div style={{ marginBlock: '12px' }}>
                <MessageBar intent="info" layout="multiline">
                  <MessageBarBody>
                    {t('admin.teamsPermissions.noTokenMessage')}
                  </MessageBarBody>
                </MessageBar>
                {/* A full-page navigation: the server has to take the browser through Entra ID and back. */}
                <Button appearance="primary" as="a" href={TEAMS_CONNECT_URL} style={{ marginTop: '12px' }}>
                  {t('admin.teamsPermissions.connect.button')}
                </Button>
              </div>
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
  const [searchParams, setSearchParams] = useSearchParams();

  // Read once. The outcome describes the round trip the admin has just come back from, so it comes
  // out of the address bar straight away: a refresh or a bookmark mustn't show a stale failure.
  const [connectOutcome] = useState(() => readTeamsConnectOutcome(searchParams));
  useEffect(() => {
    const remaining = withoutTeamsConnectOutcome(searchParams);
    if (remaining) {
      setSearchParams(remaining, { replace: true });
    }
  }, [searchParams, setSearchParams]);

  return <TeamsPermissionsPageInner t={t} connectOutcome={connectOutcome} />;
}
