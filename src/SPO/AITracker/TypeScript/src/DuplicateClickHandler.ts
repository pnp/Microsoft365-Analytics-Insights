import { ClickData } from "./Definitions";
import { debug } from "./Logger";

// Handles click & mousedown events for same click target. The 2nd event will be ignored.
// Some clicked elements can only be detected with click (megamenu links) and others only with mousedown
export class DuplicateClickHandler {

    _lastClick?: ClickData;
    _lastDate?: Date;

    registerClick(d: ClickData, callBack: Function) {
        let dupClick = false;
        if (this._lastClick && this._lastDate) {
            if (d.altText === this._lastClick.altText &&
                d.classNames === this._lastClick.classNames &&
                d.href === this._lastClick.href &&
                d.linkText === this._lastClick.linkText) {

                // All attribs are the same. Did we just click on this? (Within the same second, as this always meant)
                dupClick = Date.now() - this._lastDate.getTime() < 1000;
            }
        }

        if (!dupClick) {
            callBack();
        } else {
            debug(`Ignoring duplicate click/mousedown event for "${d.linkText || d.href || 'unknown'}"`);
        }

        this._lastDate = new Date();
        this._lastClick = d;
    }
}
