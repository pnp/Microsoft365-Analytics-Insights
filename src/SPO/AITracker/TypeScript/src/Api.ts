import { debug, error } from "./Logger"

export function getApiReturnJson<T>(url: string): Promise<T> {
    return callApiReturnString(url, "GET").then(t=> JSON.parse(t));
}

export function postApiReturnJson<T>(url: string): Promise<T> {
    return callApiReturnString(url, "POST").then(t=> JSON.parse(t));
}

export function callApiReturnString(url: string, method : string): Promise<string> {

    debug(`Calling URL '${url}'...`);
    return fetch(url, {
        headers: {
            'Accept': 'application/json;odata=verbose',
            'Content-Type': 'application/json'
        },
        method: method
    })
    .catch((err) => {
        const networkError = `Network error ${method}ing from API '${url}': ${err}`;
        error(networkError);
        return Promise.reject(networkError);
    })
    .then(async response => {
        if (response.ok) {
            const dataText: string = await response.text();
            debug(`Success ${method}ing from API '${url}'`);
            return Promise.resolve(dataText);
        }
        else {
            const dataText: string = await response.text();
            const errorTitle = `Error ${response.status} ${method}ing from API '${url}'`;
            let errorText = "";
            if (dataText !== "")
                errorText = `${errorTitle}: ${dataText}`
            else
                errorText = errorTitle;

            error(errorText);

            return Promise.reject(dataText);
        }
    });
}