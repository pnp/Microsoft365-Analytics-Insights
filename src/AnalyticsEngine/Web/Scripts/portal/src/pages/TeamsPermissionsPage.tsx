import React from 'react';
import { Title3, Subtitle1, Text, MessageBar, MessageBarBody } from '@fluentui/react-components';
import TeamList from '../components/teams/TeamList';
import LoginControls from '../components/teams/LoginControls';
import Spinner from '../components/Spinner';
import { acquireGraphToken } from '../auth/Engine';
import { GraphResponse } from '../types/GraphResponse';
import { msalApp, fetchMsGraph, inIframe, GRAPH_ENDPOINTS } from '../auth/auth-utils';
import type { AccountInfo, AuthenticationResult } from '@azure/msal-browser';
import type { User, Team } from '@microsoft/microsoft-graph-types';

type TeamsPermissionsState = {
  loading: boolean;
  accountFromMSAL: AccountInfo | null;
  error: string | null;
  joinedTeams: Array<Team> | null;
  graphProfile: User | null;
  serverSideToken: AuthenticationResult | null;
};

/**
 * Authorise / de-authorise Teams for deep analytics. Ported from the original single-page
 * app. Works in two auth modes:
 *   1. Server-side: an AJAX call to a same-site web API returns an OAuth token (unintrusive).
 *   2. Client-side MSAL fallback if (1) is unavailable.
 */
export default class TeamsPermissionsPage extends React.Component<{}, TeamsPermissionsState> {
  constructor(props: {}) {
    super(props);
    this.state = {
      loading: true,
      accountFromMSAL: null,
      error: null,
      joinedTeams: null,
      graphProfile: null,
      serverSideToken: null,
    };
  }

  async loadTeamsData(tokenResponse: AuthenticationResult) {
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

  errorCallback = (err: string) => {
    this.setState({ error: err });
  };

  loggedInCallback = (tokenResponse: AuthenticationResult) => {
    return this.loadTeamsData(tokenResponse);
  };

  // React events
  async componentDidMount() {
    // This component works with auth in two different modes.
    // 1: We do an AJAX callback to a web API in the same website for an OAuth token, now we've logged in. Nice & unintrusive.
    // 2: If #1 fails, we enable MSAL logins.

    // Try and get server token from our ASP.Net API
    const serverSideTokenResponse = await fetch(window.o365AnalyticsTokenAPI, {
      method: 'POST',
      credentials: 'same-origin',
    }).catch((error) => {
      // If we're building in react, this is normal as it's outside ASP.Net
      console.error("Couldn't get server-side OAuth token from website.");
      console.error(error);
    });

    let serverSideToken: AuthenticationResult | null = null;
    if (serverSideTokenResponse && serverSideTokenResponse.ok) {
      serverSideToken = await serverSideTokenResponse.json().catch((error: unknown) => {
        console.error('Error deserialising server-side OAuth token:');
        console.error(error);
      });
    }

    if (!serverSideToken) {
      console.log('No OAuth token from server. Enabling MSAL logins in JavaScript.');

      // Get account code
      const accounts = msalApp.getAllAccounts();
      if (accounts.length > 0) {
        const account = accounts[0];
        this.setState({ accountFromMSAL: account });

        if (account && !inIframe()) {
          // Get OAuth code from account
          const tokenResponse = await acquireGraphToken();

          if (tokenResponse) {
            console.log('Got pre-loaded OAuth token from MSAL JS');
            return this.loadTeamsData(tokenResponse);
          }
        }
      } else {
        // No credentials either from server or MSAL. Can't load anything.
        this.setState({ loading: false });
      }
    } else {
      console.log('Got OAuth token from server.');
      this.setState({ serverSideToken: serverSideToken });
      return this.loadTeamsData(serverSideToken);
    }
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
            {!this.state.serverSideToken && (
              // No server-side auth done/possible. Inject client-side auth controls
              <LoginControls
                errorCallBack={this.errorCallback}
                loggedInCallBack={this.loggedInCallback}
                account={this.state.accountFromMSAL}
              />
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
                <Text block>Click 'Sign-In' to see your Teams</Text>
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
