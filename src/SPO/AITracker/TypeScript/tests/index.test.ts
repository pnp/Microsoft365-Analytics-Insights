/**
 * @jest-environment jsdom
 */

import { InMemoryPageStateManager } from '../src/PageProps/PageState';
import { LocalStoragePageStateManager } from '../src/PageProps/SpoImplementation/LocalStoragePageStateManager';
import { MetadataInfo } from '../src/PageProps/Models/MetadataInfo';
import { TestConfigLoader, TestPageDataService, TestPagePropertyManager } from './MockLoaders';
import { PageProps } from '../src/PageProps/Models/PageProps';
import { isValidGuid, uuidv4 } from '../src/DataFunctions';
import { DuplicateClickHandler } from '../src/DuplicateClickHandler';
import { ClickData } from '../src/Definitions';
import { ConfigHandler } from '../src/ConfigHandler';
import { AITrackerConfig } from '../src/Models';

const listTitle: string = "list 1";
const pageItemId: number = 2;
const url: string = 'https://whatever';

describe('Page metadata tests', () => {
  test('InMemoryPageStateManager', () => {

    const m = new InMemoryPageStateManager();

    expect(m.pageSeen(listTitle, pageItemId)).toBeFalsy();
    m.registerPageSeen(listTitle, pageItemId);
    expect(m.pageSeen(listTitle, pageItemId)).toBeTruthy();

  });

  test('LocalStoragePageStateManager', () => {
    const m = new LocalStoragePageStateManager();

    expect(m.pageSeen(listTitle, pageItemId)).toBeFalsy();
    m.registerPageSeen(listTitle, pageItemId);
    expect(m.pageSeen(listTitle, pageItemId)).toBeTruthy();

  });

  // PagePropertyManager tests
  test('PagePropertyManager', () => {
    const testVal = "1232";
    const stateManager = new InMemoryPageStateManager();
    const m = new TestPagePropertyManager(testVal, stateManager, new TestPageDataService());

    // Handle new page nav & then check statemanager has seen page
    m.handleNewPage(pageItemId, url, listTitle, (loadedProps: PageProps) => {
      expect(loadedProps).toBeDefined();
    }).then(pagePropsLoaded => {
      expect(stateManager.pageSeen(listTitle, pageItemId)).toBeTruthy();
      expect(pagePropsLoaded).toBeTruthy();
    });

    // Try navigating to a url that's not got a page ID
    m.handleNewPage(-1, url).then(pagePropsLoaded => {
      expect(pagePropsLoaded).toBeFalsy();
    });

    // ..or title
    m.handleNewPage(1, url).then(pagePropsLoaded => {
      expect(pagePropsLoaded).toBeFalsy();
    });
  });
});

describe('DataFunctions tests', () => {
  test('isValidGuid', () => {

    expect(isValidGuid("f5b7ced7-2039-47f9-a22d-32c66d2eec65")).toBeTruthy();
    expect(isValidGuid("")).toBeFalsy();
    expect(isValidGuid(null)).toBeFalsy();
  }),
    test('uuidv4', () => {
      expect(uuidv4() !== '').toBeTruthy();
    })
});

describe('Config load tests', () => {
  test('ConfigHandler', () => {
    const m = new ConfigHandler(new TestConfigLoader());

    m.clearConfigCache();
    expect(m.haveValidCachedConfig()).toBeFalsy();

    // Cache a default config
    m.setConfigCache(AITrackerConfig.GetDefault());
    expect(m.haveValidCachedConfig()).toBeTruthy();

    // Clear again
    m.clearConfigCache();
    expect(m.haveValidCachedConfig()).toBeFalsy();

    m.getConfigFromCacheOrAppService().then(config => {
      expect(config).toBeDefined();

      // Load from cache and check it's valid
      m.setConfigCache(AITrackerConfig.GetDefault());
      expect(m.haveValidCachedConfig()).toBeTruthy();
    });
  });
});

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

describe('DuplicateClickHandler tests', () => {
  test('registerClick', async () => {

    let count: number = 0;
    const c: DuplicateClickHandler = new DuplicateClickHandler();

    const clickData1: ClickData = { altText: "alt", classNames: "", href: "url", linkText: "link1" };
    const clickData2: ClickData = { altText: "alt", classNames: "", href: "url", linkText: "link2" };

    // Click twice quickly
    c.registerClick(clickData1, () => count++);
    c.registerClick(clickData1, () => count++);
    expect(count).toEqual(1);

    // Wait a second and try again
    await new Promise((r) => setTimeout(r, 1000));
    c.registerClick(clickData1, () => count++);
    expect(count).toEqual(2);

    // Instantly try a new link
    c.registerClick(clickData2, () => count++);
    expect(count).toEqual(3);
  })
});
