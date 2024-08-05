import { splitByIntoJsonArraysOfMaxBytes } from '../src/functions';

describe('Function tests', () => {
  test('splitByIntoJsonArraysOfMaxBytes', () => {

    // Test case 1: Empty array
    testArray([], 100);

    // Test case 2: Array with single element. Will not be split
    testArray([1], 100);

    // Test case 3: Array that will have to be split into several arrays
    testArray(["string1", "string 2", "string 3"], 5);
  });

  function testArray(arr: any[], maxBytesSize: number) {
    let result: any[] = [];
    splitByIntoJsonArraysOfMaxBytes(arr, maxBytesSize, (chunk: []) => {
      result = result.concat(chunk)
    });

    expect(result).toEqual(arr);
    result = [];
  }
});
