/**
 * @jest-environment jsdom
 */

import { InMemoryPageStateManager } from '../src/PageProps/PageState';
import { LocalStoragePageStateManager } from '../src/PageProps/SpoImplementation/LocalStoragePageStateManager';
import { TestPageDataService, TestPagePropertyManager } from './MockLoaders';
import { PageProps } from '../src/PageProps/Models/PageProps';
import { isValidGuid, uuidv4 } from '../src/DataFunctions';
import { DuplicateClickHandler } from '../src/DuplicateClickHandler';
import { ClickData } from '../src/Definitions';

const listTitle: string = "list 1";
const pageItemId: number = 2;
const url: string = 'https://whatever';

describe('Page metadata tests', () => {
  test('InMemoryPageStateManager', () => {

    const m = new InMemoryPageStateManager();

    expect(m.pageSeen(listTitle, pageItemId)).toBeFalsy();
    const dateRegistered = m.registerPageSeen(listTitle, pageItemId).getTime();
    const dateSeen = m.pageSeen(listTitle, pageItemId)!.getTime();
    expect(dateSeen === dateRegistered).toBeTruthy();

  });

  test('LocalStoragePageStateManager', () => {
    const m = new LocalStoragePageStateManager();

    m.clear();
    expect(m.pageSeen(listTitle, pageItemId)).toBeFalsy();
    const dateRegistered = m.registerPageSeen(listTitle, pageItemId).getTime();
    const dateSeen = m.pageSeen(listTitle, pageItemId);
    expect(dateSeen).toBeDefined(); // Not null

    const dateSeenTime = dateSeen!.getTime();
    expect(dateSeenTime === dateRegistered).toBeTruthy();


  });

  // PagePropertyManager tests
  test('PagePropertyManager', () => {
    const testVal = "1232";
    const stateManager = new InMemoryPageStateManager();
    const m = new TestPagePropertyManager(testVal, stateManager, new TestPageDataService());

    // Make sure page hasn't been seen before
    expect(stateManager.pageSeen(listTitle, pageItemId) === null).toBeTruthy();

    // Handle new page nav & then check statemanager has seen page
    m.handleNewPage(pageItemId, url, listTitle, (loadedProps: PageProps) => {
      expect(loadedProps).toBeDefined();
    }).then(pagePropsLoaded => {
      expect(stateManager.pageSeen(listTitle, pageItemId)).toBeTruthy();
      expect(pagePropsLoaded).toBeTruthy();


      // Handle same legit page nav as before. This time we shouldn't load the page props again
      m.handleNewPage(pageItemId, url, listTitle).then(pagePropsLoadedAgain => {
        expect(pagePropsLoadedAgain).toBeFalsy();

        // Now override the page update interval to make sure page cache marker will have expired in value and check we load again properties
        m.setPageUpdateIntervalMinutes(-1);

        m.handleNewPage(pageItemId, url, listTitle).then(pagePropsLoadedOneMoreTime => {
          expect(pagePropsLoadedOneMoreTime).toBeTruthy();
  
        });
      });

    });

    // Try navigating to a url that's not got a page ID - pagePropsLoaded should be false
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
