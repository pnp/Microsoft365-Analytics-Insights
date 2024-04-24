import { isValidGuid } from "../../DataFunctions";


export class MetadataInfo {

    id: string;
    label: string;
    propName: string;

    // Input fieldValue example:
    //      2;#Term 2|f5b7ced7-2039-47f9-a22d-32c66d2eec65
    // Invalid:
    //      6;#i:0#.f|membership|admin@m365x72460609.onmicrosoft.com
    public static FromFieldValue(propName: string, fieldValue: string): MetadataInfo {

        if (typeof (fieldValue) === "string") {
            const SEP_1ST = ";#";
            const firstSepIdx = fieldValue.indexOf(SEP_1ST);
            if (firstSepIdx > -1) {
                const SEP_2ND = "|";
                const pipeIdx = fieldValue.indexOf(SEP_2ND);
                if (pipeIdx > -1) {
                    const label = fieldValue.substring(firstSepIdx + SEP_1ST.length, pipeIdx);
                    const id = fieldValue.substring(pipeIdx + SEP_2ND.length, fieldValue.length);

                    if (isValidGuid(id)) {
                        return new MetadataInfo(propName, id, label);
                    }
                }
            }
        }

        return new MetadataInfo("", "", "");
    }
    constructor(propName: string, id: string, label: string) {
        this.id = id;
        this.label = label;
        this.propName = propName;
    }

    isValid(): boolean {
        return this.id !== "" && this.label !== "" && this.propName !== "";
    }

}
