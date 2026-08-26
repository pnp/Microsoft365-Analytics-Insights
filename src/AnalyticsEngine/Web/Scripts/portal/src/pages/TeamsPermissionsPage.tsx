import React from 'react';
import { Title3, Subtitle1, Text, MessageBar, MessageBarBody } from '@fluentui/react-components';
import TeamList from '../components/teams/TeamList';
import Spinner from '../components/Spinner';
import { GraphResponse } from '../types/GraphResponse';
import { fetchMsGraph, GRAPH_ENDPOINTS } from '../auth/graph';
import type { GraphAccessToken } from '../types/graphToken';
import type { User, Team } from '@microsoft/microsoft-graph-types';

type TeamsPermissionsState = {
  loading: boolean;
  error: string | null;
  /** Set when the site couldn't mint a Graph token for this session - nothing can load without one. */
  noToken: boolean;
  joinedTeams: Array<Team> | null;
  graphProfile: User | null;
  serverSideToken: GraphAccessToken | null;
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
 */
export default class TeamsPermissionsPage extends React.Component<{}, TeamsPermissionsState> {
  constructor(props: {}) {
    super(props);
    this.state = {
      loading: true,
      error: null,
      noToken: false,
      joinedTeams: null,
      graphProfile: null,
      serverSideToken: null,
    };
  }

  async loadTeamsData(tokenResponse: GraphAccessToken) {
    // Save token for API call
    window.o365AnalyticsTeamsToken = tokenResponse;

    // Get profile
    const graphProfile: User = await fetchMsGraph(GRAPH_ENDPOINTS.ME, tokenResponse.accessToken).catch(() => {
      this.setState({ error: 'Unable to fetch Graph profile.' });
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
    const serverSideTokenResponse = await fetch(window.o365AnalyticsTokenAPI, {
      method: 'POST',
      credentials: 'same-origin',
    }).catch((error) => {
      // Expected when running the SPA outside ASP.NET (e.g. the Vite dev server).
      console.error("Couldn't get server-side OAuth token from website.");
      console.error(error);
    });

    let serverSideToken: GraphAccessToken | null = null;
    if (serverSideTokenResponse && serverSideTokenResponse.ok) {
      serverSideToken = await serverSideTokenResponse.json().catch((error: unknown) => {
        console.error('Error deserialising server-side OAuth token:');
        console.error(error);
      });
    }

    if (!serverSideToken) {
      this.setState({ loading: false, noToken: true });
      return;
    }

    this.setState({ serverSideToken });
    return this.loadTeamsData(serverSideToken);
  }

  async getJoinedTeams(accessToken: string) {
    console.log('Loading teams for user from Graph');
    const joinedTeamsResponse: GraphResponse<Team> = await fetchMsGraph(
      GRAPH_ENDPOINTS.JOINED_TEAMS,
      accessToken,
    ).catch(() => {
      this.setState({ error: 'Unable to fetch joined teams.' });
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
    return (
      <div>
        <Title3 block>Grant Team Access to the Microsoft 365 Advanced Analytics Engine</Title3>
        <div style={{ height: 16 }} />
        {this.state.loading ? (
          <div style={{ textAlign: 'center', padding: '32px' }}>
            <Spinner size={100} label="Loading your Teams..." />
          </div>
        ) : (
          <div>
            {this.state.noToken && (
              <MessageBar intent="error" style={{ marginBlock: '12px' }}>
                <MessageBarBody>
                  The site couldn't get a Microsoft Graph token for your session, so your Teams can't be listed. This
                  usually means the sign-in that captured your refresh token has expired or predates it - sign out and
                  sign in again. If it keeps happening, check that the runtime app registration has the delegated Teams
                  permissions and that the site's reply URL is registered.
                </MessageBarBody>
              </MessageBar>
            )}

            {this.state.error && (
              <MessageBar intent="error" style={{ marginBlock: '12px' }}>
                <MessageBarBody>{this.state.error}</MessageBarBody>
              </MessageBar>
            )}

            <Text block style={{ marginBlock: '12px' }}>
              This page is so you can authorise deep analytics for a Team. This will allow Microsoft 365 Advanced
              Analytics and Insights to read messages for anonymous statistical reporting purposes only.
            </Text>

            <section>
              {this.state.joinedTeams ? (
                <div>
                  <Subtitle1 block style={{ marginBlock: '12px' }}>
                    Your Teams - {this.state.graphProfile?.displayName}
                  </Subtitle1>
                  <Text block style={{ marginBottom: '8px' }}>
                    Here are all the Teams you have access to. Select which Teams you want to enable for deep analytics
                    and continue.
                  </Text>
                  <TeamList teamsList={this.state.joinedTeams} />
                </div>
              ) : (
                <Text block>
                  {this.state.noToken
                    ? 'Your Teams will be listed here once the site can get a Graph token for your session.'
                    : 'No Teams found for your account.'}
                </Text>
              )}
            </section>
            <Text block size={200} style={{ marginTop: '12px', color: 'var(--colorNeutralForeground3)' }}>
              Note: tokens are securely stored in a temporary Redis cache &amp; aren't accessible to anyone.
            </Text>
          </div>
        )}
      </div>
    );
  }
}
