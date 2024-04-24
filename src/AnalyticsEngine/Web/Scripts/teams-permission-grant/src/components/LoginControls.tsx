import React from "react";
import {
    msalApp,
    GRAPH_REQUESTS
} from "./../auth-utils";

import { acquireGraphToken } from '../Engine';


export default class LoginControls extends React.Component<{ errorCallBack: Function; loggedInCallBack: Function; account: any }, {}> {

    constructor(props: any) {
        super(props);
        this.props.loggedInCallBack.bind(this);
    }

    // Sign-in and get Graph token
    async onSignIn() {
        console.log("onSignIn");

        const loginResponse = await msalApp
            .loginPopup(GRAPH_REQUESTS.LOGIN)
            .catch(error => {
                this.props.errorCallBack(error.message);
            });


        if (loginResponse) {
            this.setState({
                account: loginResponse.account,
            });

            const tokenResponse = await acquireGraphToken()
                .catch(error => {
                    this.props.errorCallBack(error.message);
                });

            console.log('Got OAuth token from MSAL JS on sign-in.');
            console.log(tokenResponse);

            if (tokenResponse) {
                await this.props.loggedInCallBack(tokenResponse);
            }
        }
    }

    onSignInClick = () => {
        this.onSignIn();
    }
    onSignOut() {
        msalApp.logoutPopup().then(() => { window.location.reload(); });
    }

    render() {
        return <div>
            <span>No server-side credentials found. Authenticate with client-side to use application.</span>
            <span>
                {this.props.account ? (
                    <button type="button" id="signOut" className="btn btn-secondary"
                        onClick={this.onSignOut}>Sign Out</button>
                ) : (<div>
                    <button type="button" className="btn btn-primary"
                        onClick={this.onSignInClick}>Sign In</button>
                </div>)}
            </span>
            
        </div>
    }
}
