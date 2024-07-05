/**
 * @jest-environment jsdom
 */

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
    test('PageProps', () => {

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
});
