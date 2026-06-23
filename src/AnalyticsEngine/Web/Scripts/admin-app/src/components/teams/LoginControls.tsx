import React from 'react';
import { msalApp, GRAPH_REQUESTS } from '../../auth/auth-utils';
import { acquireGraphToken } from '../../auth/Engine';
import type { AccountInfo, AuthenticationResult } from '@azure/msal-browser';

type LoginControlsProps = {
  errorCallBack: (message: string) => void;
  loggedInCallBack: (token: AuthenticationResult) => void | Promise<void>;
  account: AccountInfo | null;
};

export default class LoginControls extends React.Component<LoginControlsProps> {
  // Sign-in and get a Graph token
  async onSignIn() {
    const loginResponse = await msalApp.loginPopup(GRAPH_REQUESTS.LOGIN).catch((error) => {
      this.props.errorCallBack(error.message);
    });

    if (loginResponse) {
      const tokenResponse = await acquireGraphToken().catch((error) => {
        this.props.errorCallBack(error.message);
      });

      if (tokenResponse) {
        await this.props.loggedInCallBack(tokenResponse);
      }
    }
  }

  onSignInClick = () => {
    this.onSignIn();
  };

  onSignOut = () => {
    msalApp.logoutPopup().then(() => {
      window.location.reload();
    });
  };

  render() {
    return (
      <div>
        <span>No server-side credentials found. Authenticate with client-side to use the application.</span>
        <span>
          {this.props.account ? (
            <button type="button" id="signOut" className="btn btn-secondary" onClick={this.onSignOut}>
              Sign Out
            </button>
          ) : (
            <button type="button" className="btn btn-primary" onClick={this.onSignInClick}>
              Sign In
            </button>
          )}
        </span>
      </div>
    );
  }
}
