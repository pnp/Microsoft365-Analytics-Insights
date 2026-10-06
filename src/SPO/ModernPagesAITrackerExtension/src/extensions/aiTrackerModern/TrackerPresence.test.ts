import { loadTrackerUnlessRunning, trackerAlreadyRunning } from './TrackerPresence';

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

// A page: its window, the state on it, and a way to fire its load event
interface IFakePage {
  window: Window;
  state: Record<string, unknown>;
  fireLoad: () => void;
}

function fakePage(readyState: string): IFakePage {
  const loadListeners: (() => void)[] = [];
  const document: { readyState: string } = { readyState };
  const state: Record<string, unknown> = {
    document,
    addEventListener: (type: string, listener: () => void): void => {
      if (type === 'load') {
        loadListeners.push(listener);
      }
    },
    setTimeout: (handler: () => void, ms: number): ReturnType<typeof setTimeout> => setTimeout(handler, ms),
    clearTimeout: (timer: ReturnType<typeof setTimeout>): void => clearTimeout(timer)
  };
  return {
    window: state as unknown as Window,
    state,
    fireLoad: (): void => {
      document.readyState = 'complete';
      loadListeners.splice(0).forEach(listener => listener());
    }
  };
}

// The load of one site's copy, which the test finishes: the browser runs the copy, then the loader resolves
interface IScriptLoad {
  load: jest.Mock<Promise<void>, []>;
  arrive: (runCopy?: () => void) => void;
  fail: () => void;
}

function scriptLoad(): IScriptLoad {
  let resolveLoad: () => void = () => undefined;
  let rejectLoad: (e: Error) => void = () => undefined;
  const load = jest.fn((): Promise<void> => new Promise<void>((resolve, reject) => {
    resolveLoad = resolve;
    rejectLoad = reject;
  }));
  return {
    load,
    arrive: (runCopy?: () => void): void => {
      if (runCopy) {
        runCopy();
      }
      resolveLoad();
    },
    fail: (): void => rejectLoad(new Error('404 Not Found'))
  };
}

// Lets pending promise callbacks and zero-length timeouts run
async function settle(): Promise<void> {
  await new Promise<void>(resolve => setTimeout(resolve, 20));
}

describe('loadTrackerUnlessRunning', () => {
  it("loads this site's copy when no tracker is running", async () => {
    const page = fakePage('complete');
    const copy = scriptLoad();
    const result = loadTrackerUnlessRunning(page.window, copy.load);
    expect(copy.load).toHaveBeenCalledTimes(1);

    copy.arrive(() => { page.state.spoInsightsAITrackerVersion = '1.6.3'; });
    expect(await result).toBeUndefined();
    await settle();
    expect(page.state.spoInsightsAITrackerLoading).toBeUndefined();
  });

  it("doesn't load a copy while one is tracking the page", async () => {
    const page = fakePage('complete');
    page.state.spoInsightsAITrackerVersion = '1.6.3';
    const copy = scriptLoad();

    expect(await loadTrackerUnlessRunning(page.window, copy.load)).toBe('1.6.3');
    expect(copy.load).not.toHaveBeenCalled();
  });

  it("waits for another site's copy still on its way, rather than loading its own alongside", async () => {
    // SharePoint navigated to the second site before the first site's copy arrived
    const page = fakePage('interactive');
    const first = scriptLoad();
    const second = scriptLoad();
    const firstResult = loadTrackerUnlessRunning(page.window, first.load);
    const secondResult = loadTrackerUnlessRunning(page.window, second.load);
    await settle();
    expect(second.load).not.toHaveBeenCalled();

    first.arrive(() => { page.state.spoInsightsAITrackerVersion = '1.6.3'; });
    expect(await firstResult).toBeUndefined();
    expect(await secondResult).toBe('1.6.3');
    expect(second.load).not.toHaveBeenCalled();
  });

  it('waits until just after the page has loaded for a copy that shows nothing yet, as one before 1.6.0 does', async () => {
    const page = fakePage('interactive');
    const legacy = scriptLoad();
    const next = scriptLoad();
    const legacyResult = loadTrackerUnlessRunning(page.window, legacy.load);

    // A copy before 1.6.0 tracks the page, and publishes modernPageNav, in a timeout from a load listener of its own
    legacy.arrive(() => page.window.addEventListener('load', () => setTimeout(() => { page.state.modernPageNav = (): void => undefined; }, 0)));
    expect(await legacyResult).toBeUndefined();

    const nextResult = loadTrackerUnlessRunning(page.window, next.load);
    await settle();
    expect(next.load).not.toHaveBeenCalled();

    page.fireLoad();
    expect(await nextResult).toBe('earlier than 1.6.0');
    expect(next.load).not.toHaveBeenCalled();
  });

  it("loads its own copy once the page has loaded, after a copy that couldn't start", async () => {
    const page = fakePage('interactive');
    const failed = scriptLoad();
    const next = scriptLoad();
    const failedResult = loadTrackerUnlessRunning(page.window, failed.load);
    failed.arrive();
    expect(await failedResult).toBeUndefined();

    const nextResult = loadTrackerUnlessRunning(page.window, next.load);
    await settle();
    expect(next.load).not.toHaveBeenCalled();

    page.fireLoad();
    await settle();
    expect(next.load).toHaveBeenCalledTimes(1);
    next.arrive(() => { page.state.spoInsightsAITrackerVersion = '1.6.3'; });
    expect(await nextResult).toBeUndefined();
  });

  it("loads its own copy at once after a copy that couldn't start on a page that has loaded", async () => {
    const page = fakePage('complete');
    const failed = scriptLoad();
    const next = scriptLoad();
    const failedResult = loadTrackerUnlessRunning(page.window, failed.load);
    const nextResult = loadTrackerUnlessRunning(page.window, next.load);
    failed.arrive();
    expect(await failedResult).toBeUndefined();

    await settle();
    expect(next.load).toHaveBeenCalledTimes(1);
    next.arrive(() => { page.state.spoInsightsAITrackerVersion = '1.6.3'; });
    expect(await nextResult).toBeUndefined();
  });

  it("a copy that doesn't load doesn't stop the next site loading its own", async () => {
    const page = fakePage('complete');
    const missing = scriptLoad();
    const next = scriptLoad();
    const missingResult = loadTrackerUnlessRunning(page.window, missing.load);
    const nextResult = loadTrackerUnlessRunning(page.window, next.load);

    missing.fail();
    await expect(missingResult).rejects.toThrow('404 Not Found');
    await settle();
    expect(next.load).toHaveBeenCalledTimes(1);
    next.arrive(() => { page.state.spoInsightsAITrackerVersion = '1.6.3'; });
    expect(await nextResult).toBeUndefined();
  });

  it('loads one copy at a time, however many sites are waiting', async () => {
    const page = fakePage('complete');
    const first = scriptLoad();
    const second = scriptLoad();
    const third = scriptLoad();
    const firstResult = loadTrackerUnlessRunning(page.window, first.load);
    const secondResult = loadTrackerUnlessRunning(page.window, second.load);
    const thirdResult = loadTrackerUnlessRunning(page.window, third.load);

    first.fail();
    await expect(firstResult).rejects.toThrow('404 Not Found');
    await settle();
    expect(second.load).toHaveBeenCalledTimes(1);
    expect(third.load).not.toHaveBeenCalled();

    second.arrive(() => { page.state.spoInsightsAITrackerVersion = '1.6.3'; });
    expect(await secondResult).toBeUndefined();
    expect(await thirdResult).toBe('1.6.3');
    expect(third.load).not.toHaveBeenCalled();
  });

  it('stops waiting for a load that never finishes', async () => {
    const page = fakePage('complete');
    const stuck = scriptLoad();
    const next = scriptLoad();
    const stuckResult = loadTrackerUnlessRunning(page.window, stuck.load);
    const nextResult = loadTrackerUnlessRunning(page.window, next.load, 50);
    await settle();
    expect(next.load).not.toHaveBeenCalled();

    await new Promise<void>(resolve => setTimeout(resolve, 100));
    expect(next.load).toHaveBeenCalledTimes(1);

    // The stuck load finishing late leaves the page to the copy now loading
    stuck.arrive();
    expect(await stuckResult).toBeUndefined();
    await settle();
    expect(page.state.spoInsightsAITrackerLoading).toBeDefined();

    next.arrive(() => { page.state.spoInsightsAITrackerVersion = '1.6.3'; });
    expect(await nextResult).toBeUndefined();
  });
});
