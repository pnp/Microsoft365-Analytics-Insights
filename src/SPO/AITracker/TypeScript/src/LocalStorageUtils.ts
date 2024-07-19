
export class LocalStorageUtils  {
    
    static isLocalStorageAvailable() {
        var test = 'localstorage test';
        try {
            localStorage.setItem(test, test);
            localStorage.removeItem(test);
            return true;
        } catch (e) {
            return false;
        }
    }
}
