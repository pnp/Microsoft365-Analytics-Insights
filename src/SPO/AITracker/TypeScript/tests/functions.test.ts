import { splitIntoJsonArraysOfMaxBytes } from '../src/functions';

describe('Function tests', () => {
  test('splitByIntoJsonArraysOfMaxBytes', () => {

    // Test case 1: Empty array
    testArray([], 100, 0);

    // Test case 2: Array with single element. Will not be split
    testArray([1], 100);
    testArray([1, 2], 100);

    // Test case 3: Array that will have to be split into several arrays
    testArray(["string1", "string 2", "string 3"], 5, 3);
  });

  function testArray(arr: any[], maxBytesSize: number, expectedCallBackCount: number = 1) {
    let result: any[] = [];
    let callbackCount = 0;
    splitIntoJsonArraysOfMaxBytes(arr, maxBytesSize, (chunk: []) => {
      result = result.concat(chunk);
      callbackCount++;
    });

    expect(result).toEqual(arr);
    expect(callbackCount).toBe(expectedCallBackCount);
  }
});
