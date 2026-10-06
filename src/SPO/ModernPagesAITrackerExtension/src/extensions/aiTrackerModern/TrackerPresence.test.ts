import { trackerAlreadyRunning } from './TrackerPresence';

describe('trackerAlreadyRunning', () => {
  it('finds no tracker in a page without one', () => {
    expect(trackerAlreadyRunning({} as unknown as Window)).toBeUndefined();
  });

  it('reports the version a 1.6.0 or later copy recorded as it started', () => {
    expect(trackerAlreadyRunning({ spoInsightsAITrackerVersion: '1.6.2' } as unknown as Window)).toBe('1.6.2');
  });

  it('finds an earlier copy that has tracked a page, by its modernPageNav', () => {
    expect(trackerAlreadyRunning({ modernPageNav: (): void => undefined } as unknown as Window)).toBe('earlier than 1.6.0');
  });

  it('ignores a modernPageNav that is not a function', () => {
    expect(trackerAlreadyRunning({ modernPageNav: 'not a function' } as unknown as Window)).toBeUndefined();
  });
});
