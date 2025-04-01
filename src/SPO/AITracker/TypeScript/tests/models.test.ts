
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
      "6;#i:0#.f|membership|admin@m365x72460609.onmicrosoft.com").isValid()).toBeFalsy();

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
});
