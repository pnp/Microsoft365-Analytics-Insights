import { debug, error } from "./Logger"

export function spApiJson<T>(url: string): Promise<T> {
    return spApi(url).then(t=> JSON.parse(t));
}

export function spApi(url: string): Promise<string> {

    const method = "GET";
    debug(`Calling SPO URL '${url}'...`);
    return fetch(url, {
        headers: {
            'Accept': 'application/json;odata=verbose',
            'Content-Type': 'application/json'
        },
        method: method
    })
    .then(async response => {
        if (response.ok) {
            const dataText: string = await response.text();
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

            console.warn(errorText);
            error(errorText);

            return Promise.reject(dataText);
        }
    });
}
