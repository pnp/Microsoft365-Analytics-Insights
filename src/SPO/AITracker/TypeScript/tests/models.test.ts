
import { MetadataInfo } from '../src/PageProps/Models/MetadataInfo';
import { PageProps } from '../src/PageProps/Models/PageProps';

describe('Model tests', () => {
  test('MetadataInfo', () => {

    // Parse raw prop
    expect(MetadataInfo.FromFieldValue("", "").isValid()).toBeFalsy();
    expect(MetadataInfo.FromFieldValue("", "2;#Term 2|f5b7ced7-2039-47f9-a22d-32c66d2eec65").isValid()).toBeFalsy();
    expect(MetadataInfo.FromFieldValue("taxprop", "2; Term 2|f5b7ced7-2039-47f9-a22d-32c66d2eec65").isValid()).toBeFalsy();
    expect(MetadataInfo.FromFieldValue("taxprop", "2;#Term 2|f5b7ced7-2039-47f9-a22d-32c66d2eec65").isValid()).toBeTruthy();

    // User ID field vals shouldn't be detected as a managed metadata field
    expect(MetadataInfo.FromFieldValue("OData__x005f_AuthorByline",
      "6;#i:0#.f|membership|admin@contoso.onmicrosoft.com").isValid()).toBeFalsy();

  }),
    test('PageProps parse tests', () => {

      // propsCount
      expect(new PageProps("http://url", { prop1: null }).propsCount()).toEqual(0); // Empty value

      expect(new PageProps("http://url", { propToBig: 'x'.repeat(10 * 1024 * 1024) }).propsCount()).toEqual(0);   // Too big a field
      expect(new PageProps("http://url", { prop1: null, prop2: 123 }).propsCount()).toEqual(1);                   // One valid prop (prop2)

      // taxonomyFieldRefs
      expect(new PageProps("http://url", { prop1: null }).taxonomyFieldRefs().length).toEqual(0);

      const singleTaxObj = new PageProps("http://url",
        {
          prop1: null,
          prop2: "2;#Term 2|f5b7ced7-2039-47f9-a22d-32c66d2eec65",
          TaxCatchAll: "2;#This should be ignored|f5b7ced7-2039-47f9-a22d-32c66d2eec65" // Ignore TaxCatchAll
        });

      // There's only 1 valid MM prop
      expect(singleTaxObj.taxonomyFieldRefs().length).toEqual(1);

    });
    test('PageProps splitIntoMutliple', () => {

      const p1 = new PageProps("http://url", { prop1: 123, prop2: 456 });               
      const randoPropsResult = p1.splitIntoMutliple(1);
      expect(randoPropsResult.length).toBe(2);
      expect(randoPropsResult[0].propsCount()).toBe(1);
      expect(randoPropsResult[1].propsCount()).toBe(1);

      // Props are object with array
      expect(JSON.stringify(randoPropsResult[0].props)).toBe(JSON.stringify({ prop1: 123 }));  
      expect(JSON.stringify(randoPropsResult[1].props)).toBe(JSON.stringify({ prop2: 456 }));

      // Check comments and likes are split correctly
      const p2 = new PageProps("http://url", { }, [{comment: "comment1", id: "1", email: "testemail", isReply: false, creationDate: new Date()}, 
        {comment: "comment2", id: "2", email: "testemail", isReply: false, creationDate: new Date()}]);               
      const commentsResult = p2.splitIntoMutliple(1);
      expect(commentsResult.length).toBe(2);
      expect(commentsResult[0].pageComments.length).toBe(1);
      expect(commentsResult[1].pageComments.length).toBe(1);

      
      const p3 = new PageProps("http://url", { }, undefined, [{id: "1", email: "testemail", creationDate: new Date()}, 
        {id: "2", email: "testemail", creationDate: new Date()}]);               
      const likesResult = p3.splitIntoMutliple(1);
      expect(likesResult.length).toBe(2);
      expect(likesResult[0].pageLikes.length).toBe(1);
      expect(likesResult[1].pageLikes.length).toBe(1);
    });

    // Every part used to be sent with no taxonomy fields at all, so the importer never received one
    test('PageProps splitIntoMutliple keeps the taxonomy fields', () => {
      const pp = new PageProps("http://url", {
        Department: "2;#Contoso Finance|f5b7ced7-2039-47f9-a22d-32c66d2eec65",
        Region: "3;#Contoso North|9c8d2e3a-4b5f-4a6e-8d7c-1b2a3c4d5e6f"
      });
      expect(pp.setTaxonomyFieldsFromRawLoadedProps()).toBe(2);

      // Small enough that each field goes in a part of its own
      const parts = pp.splitIntoMutliple(1);

      expect(parts.length).toBe(2);
      expect(parts[0].taxonomyProps).toEqual([{ id: "f5b7ced7-2039-47f9-a22d-32c66d2eec65", label: "Contoso Finance", propName: "Department" }]);
      expect(parts[1].taxonomyProps).toEqual([{ id: "9c8d2e3a-4b5f-4a6e-8d7c-1b2a3c4d5e6f", label: "Contoso North", propName: "Region" }]);

      // One part when everything fits, with every field in it
      const single = pp.splitIntoMutliple(8192);
      expect(single.length).toBe(1);
      expect(single[0].taxonomyProps.map(t => t.propName)).toEqual(["Department", "Region"]);
    });

    test('PageProps splitIntoMutliple sends a page with no taxonomy fields without any', () => {
      const parts = new PageProps("http://url", { Title: "Contoso home" }).splitIntoMutliple(8192);
      expect(parts.length).toBe(1);
      expect(parts[0].taxonomyProps).toEqual([]);
    });

    // App Insights sends a custom property holding an object as that object's JSON. The importer reads this exact
    // string into TaxonomoyProperty (id, label, propName): PageUpdateEventListParseTests uses the same payload.
    test('taxonomy fields are sent in the shape the importer reads', () => {
      const pp = new PageProps("http://url", { Department: "2;#Contoso Finance|f5b7ced7-2039-47f9-a22d-32c66d2eec65" });
      pp.setTaxonomyFieldsFromRawLoadedProps();

      const part = pp.splitIntoMutliple(8192)[0];

      expect(JSON.stringify(part.taxonomyProps))
        .toBe('[{"id":"f5b7ced7-2039-47f9-a22d-32c66d2eec65","label":"Contoso Finance","propName":"Department"}]');
    });
});
