import React from "react";
import "react-loader-spinner/dist/loader/css/react-spinner-loader.css";
import Loader from "react-loader-spinner";

type ConfirmSelectionProps =
    {
        saveCallback: Function;
        authCount: number;
        deAuthCount: number;
        isBusy: boolean
    }

export default class ConfirmSelection extends React.Component<ConfirmSelectionProps, {}> {

    constructor(props: any) {
        super(props);
        this.props.saveCallback.bind(this);
    }

    onSave() {
        this.props.saveCallback();
    }

    render() {
        return <div>
            <div style={{ width: "100%", overflow: "hidden" }}>
                <div style={{ float: "left", fontWeight: 600 }}>Actions to apply:</div>
                <div style={{ marginLeft: "180px" }}>
                    <div>De-authorise {this.props.deAuthCount} Team(s); Authorise {this.props.authCount} Team(s) </div>
                    <div></div>
                </div>
            </div>
            {!this.props.isBusy ?
                <Loader
                    type="Oval"
                    color="#007bff"
                    height={30}
                    width={30} />
                :
                <button type="button"
                    className="btn btn-primary"
                    onClick={this.onSave.bind(this)}>Save Changes</button>
            }
        </div>
    }
}
