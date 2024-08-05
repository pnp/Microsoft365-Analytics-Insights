import { LikesUserEntity, PageComment } from "../../Definitions";
import { splitIntoJsonArraysOfMaxBytes } from "../../functions";
import { MetadataInfo } from "./MetadataInfo";

const MAX_PROP_VAL = 1000;

export class PageProps {

    url: string;
    props: any;
    pageComments: PageComment[];
    pageLikes: LikesUserEntity[];
    taxonomyProps: MetadataInfo[];

    constructor(url: string, propsAll: any, pageComments?: PageComment[], pageLikes?: LikesUserEntity[]) {

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
        this.pageComments = pageComments ?? [];
        this.pageLikes = pageLikes ?? [];
    }

    propsCount(): number {
        return Object.keys(this.props).length;
    }

    taxonomyFieldRefs(): MetadataInfo[] {
        let defs: MetadataInfo[] = [];

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
    setTaxonomyFieldsFromRawLoadedProps(): number {
        this.taxonomyProps = this.taxonomyFieldRefs();
        return this.taxonomyProps.length;
    }

    // Splits this object into multiple PageProps objects, each with a maximum byte size
    // This is so we don't hit max request size limits - https://learn.microsoft.com/en-us/azure/azure-monitor/app/api-custom-events-metrics#limits
    splitIntoMutliple(maxBytesSize: number): PageProps[] {
        let result: PageProps[] = [];

        let taxonomyPropsArray: MetadataInfo[][] = [];
        let propsArray: any[][] = [];
        let pageCommentsArray: PageComment[][] = [];
        let pageLikesArray: LikesUserEntity[][] = [];

        splitIntoJsonArraysOfMaxBytes(this.taxonomyProps, maxBytesSize, (chunk: MetadataInfo[]) => { taxonomyPropsArray.push(chunk); });

        let propsParsedArray : any[] = [];
        Object.keys(this.props).forEach(p => propsParsedArray.push({[p]: this.props[p]}));

        splitIntoJsonArraysOfMaxBytes(propsParsedArray, maxBytesSize, (chunk: any[]) => { propsArray.push(chunk); });
        splitIntoJsonArraysOfMaxBytes(this.pageComments, maxBytesSize, (chunk: PageComment[]) => { pageCommentsArray.push(chunk); });
        splitIntoJsonArraysOfMaxBytes(this.pageLikes, maxBytesSize, (chunk: LikesUserEntity[]) => { pageLikesArray.push(chunk); });

        const maxArrayLength = Math.max(taxonomyPropsArray.length, propsArray.length, pageCommentsArray.length, pageLikesArray.length);
        for (let i = 0; i < maxArrayLength; i++) {
            const propsObj = propsArray[i] || {};
            const flattenedProps = Object.assign({}, ...propsObj);   
            const p = new PageProps(this.url, flattenedProps, pageCommentsArray[i] || [], pageLikesArray[i] || []);
            result.push(p);
        }

        return result
    }
}

export interface TermStoreProp {
    TermGuid: string;
    __metadata: FieldMetadata;
}
export interface DeferredProp {
    uri: string;
}
export interface FieldMetadata {
    uri: string;
}