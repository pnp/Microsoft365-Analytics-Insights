import React from "react";
import TeamList from './components/TeamList';
import LoginControls from './components/LoginControls';
import { acquireGraphToken } from './Engine';
import { GraphResponse } from './GraphResponse';
import {
    msalApp,
    fetchMsGraph,
    GRAPH_ENDPOINTS
} from "./auth-utils";
import { WindowUtils } from "msal";
import { AccountInfo } from "@azure/msal-common";
import { AuthenticationResult } from "@azure/msal-browser";
import { User, Team } from "@microsoft/microsoft-graph-types";
import "react-loader-spinner/dist/loader/css/react-spinner-loader.css";
import Loader from "react-loader-spinner";

type AppState = {
    loading: boolean;
    accountFromMSAL: AccountInfo | null;
    error: string | null;
    joinedTeams: Array<Team> | null;
    selectedTeam: any;
    graphProfile: User | null;
    serverSideToken: string | null;
}

declare global {
    interface Window 
    { 
        o365AnalyticsClientId: string; 
        o365AnalyticsAuthority: string;
        o365AnalyticsRedirectUri: string;
        o365AnalyticsTeamsToken: AuthenticationResult;
        o365AnalyticsTokenAPI: string;
    }
}
export default class App extends React.Component<{}, AppState> {

    constructor(props: any) {
        super(props);
        this.state = {
            loading: true,
            accountFromMSAL: null,
            error: null,
            joinedTeams: null,
            selectedTeam: null,
            graphProfile: null,
            serverSideToken: null
        };
    }

    async loadTeamsData(tokenResponse: AuthenticationResult) {

        // Save token for API call
        window.o365AnalyticsTeamsToken = tokenResponse;

        // Get profile
        const graphProfile : User = await fetchMsGraph(
            GRAPH_ENDPOINTS.ME,
            tokenResponse.accessToken
        ).catch(() => {
            this.setState({
                error: "Unable to fetch Graph profile."
            });
        });

        if (graphProfile) {
            this.setState({
                graphProfile
            });
        }


        // Get teams
        return this.getJoinedTeams(tokenResponse.accessToken);

    }


    errorCallback(err: string) {
        this.setState({
            error: err
        });
    }

    // React events
    async componentDidMount() {

        // This component works with auth in two different modes.
        // 1: We do an AJAX callback to a web API in the same website for an OAuth token, now we've logged in. Nice & unintrusive.
        // 2: If #1 fails, we enabled MSAL logins.

        // Try and get server token from our ASP.Net API
        var serverSideTokenResponse = await fetch(window.o365AnalyticsTokenAPI, {
            method: "POST",
            credentials: "same-origin"
        }).catch(error => {

            // If we're building in react, this is normal as it's outside ASP.Net
            console.error("Couldn't get server-side OAuth token from website.");
            console.error(error);
        });

        var serverSideToken = null;
        if (serverSideTokenResponse && serverSideTokenResponse.ok) {
            serverSideToken = await serverSideTokenResponse.json()
                .catch(error => {
                    console.error("Error deserialising server-side OAuth token:");
                    console.error(error);
                });
        }

        if (!serverSideToken) {
            console.log("No OAuth token from server. Enabling MSAL logins in JavaScript.");

            // Get account code
            const accounts = msalApp.getAllAccounts();
            if (accounts.length > 0) {
                const account = accounts[0];
                this.setState({
                    accountFromMSAL: account
                });

                if (account && !WindowUtils.isInIframe()) {

                    // Get OAuth code from account
                    const tokenResponse = await acquireGraphToken();

                    if (tokenResponse) {
                        console.log('Got pre-loaded OAuth token from MSAL JS');
                        console.log(tokenResponse);
                        return this.loadTeamsData(tokenResponse);
                    }
                }
            }
            else {
                // No credentials either from server or MSAL. Can't load anything.
                this.setState({ loading: false });
            }
        }
        else {
            console.log("Got OAuth token from server.");
            console.log(serverSideToken);
            this.setState({ serverSideToken: serverSideToken });
            return this.loadTeamsData(serverSideToken);
        }

    }

    async getJoinedTeams(accessToken: string) {
        console.log("Loading teams for user from Graph");
        const joinedTeamsResponse : GraphResponse<Team> = await fetchMsGraph(
            GRAPH_ENDPOINTS.JOINED_TEAMS,
            accessToken
        ).catch(() => {
            this.setState({
                error: "Unable to fetch joined teams."
            });
        });


        if (joinedTeamsResponse) {

            this.setState({
                joinedTeams: joinedTeamsResponse.value,
                error: null
            });
        }

        this.setState({ loading: false });
    }


    render() {

        return (
            <div>
                {this.state.loading ? (
                    <div className="text-center">
                        <Loader
                            type="Oval"
                            color="#007bff"
                            height={100}
                            width={100}
                        />
                    </div>
                ) :
                    <div>
                        {!this.state.serverSideToken &&
                            (
                                // No server-side auth done/possible. Inject client-side auth controls
                                <LoginControls errorCallBack={() => this.errorCallback} 
                                    loggedInCallBack={() => this.loadTeamsData} 
                                    account={this.state.accountFromMSAL} />
                            )
                        }

                        {this.state.error && (
                            <p className="error">Error: {this.state.error}</p>
                        )}

                        <p>
                            This page is so you can authorise deep analytics for a Team.
                            This will allow Office 365 Advanced Analytics and Insights to read messages for anonymous statistical reporting purposes only.
                        </p>

                        <section className="data">
                            {this.state.joinedTeams ? (
                                
                                <div>
                                    <h2>Your Teams - {this.state.graphProfile?.displayName}</h2>
                                    <p>
                                        Here are all the Teams you have access to. 
                                        Select which Teams you want to enable for deep analytics and continue.
                                    </p>
                                    <TeamList teamsList={this.state.joinedTeams} />
                                </div>
                            ) : (<div>Click 'Sign-In' to see your Teams</div>)}
                        </section>
                        <p>Note: tokens are securely stored in a temporary redis cache &amp; aren't accessible to anyone.</p>
                    </div>
                }
            </div>
        );
    }
}
