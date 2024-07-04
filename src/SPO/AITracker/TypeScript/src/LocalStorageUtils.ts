import { debug } from "./Logger";

const moment = require('moment');

export class LocalStorageUtils  {
    
    static isLocalStorageAvailable() {
        var test = 'localstorage test';
        try {
            localStorage.setItem(test, test);
            localStorage.removeItem(test);

            debug("Local storage available");
            return true;
        } catch (e) {
            debug("Local storage not available");
            return false;
        }
    }
}
