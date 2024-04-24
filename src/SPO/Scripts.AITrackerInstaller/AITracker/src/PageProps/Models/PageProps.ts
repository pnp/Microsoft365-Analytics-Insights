import { MetadataInfo } from "./MetadataInfo";

export class PageProps {

    url: string;
    props: any;
    taxonomyProps: MetadataInfo[];

    constructor(url : string, propsAll: any) {

        const MAX_PROP_VAL = 1000;
        let validProps = {};

        // Validate each property
        Object.keys(propsAll).forEach(key => {
            const keyVal = propsAll[key];

            // If it's not null, and it's a string below a certain length or a number
            if (keyVal && (keyVal.length && keyVal.length <= MAX_PROP_VAL) || typeof keyVal === "number") {
                validProps[key] = keyVal;
            }

        });

        this.props = validProps;
        this.url = url;
        this.taxonomyProps = [];
    }

    propsCount() : number
    {
        return Object.keys(this.props).length;
    }

    taxonomyFieldRefs() : MetadataInfo[]
    {
        let defs : MetadataInfo[] = [];

        Object.keys(this.props).forEach(propName => {
            const keyVal = this.props[propName];
            if (keyVal) {
                const v: MetadataInfo = MetadataInfo.FromFieldValue(propName, keyVal);
                if (v.isValid() && v.propName !== "TaxCatchAll") {      // Ignore TaxCatchAll field so we don't get duplicate props
                    defs.push(v);
                }
            }
        });

        return defs;
    }
    setTaxonomyFieldsFromRawLoadedProps() : number {
        this.taxonomyProps = this.taxonomyFieldRefs();
        return this.taxonomyProps.length;
    }

}
export interface TermStoreProp
{
    TermGuid: string;
    __metadata: FieldMetadata;
}
export interface DeferredProp
{
    uri: string;
}
export interface FieldMetadata
{
    uri: string;
}