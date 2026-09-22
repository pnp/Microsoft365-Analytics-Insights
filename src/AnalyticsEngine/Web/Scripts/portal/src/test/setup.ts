import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// Unmount anything a test rendered, so a component left mounted by one test can't leak DOM,
// timers or module state into the next one.
afterEach(() => {
  cleanup();
});

// A few suites run under the `node` environment rather than jsdom - the ones that read source
// files off disk rather than render anything, where paying jsdom's ~50s start-up per file buys
// nothing. There is no `window` there, so the shims below have to check before reaching for it.
const hasDom = typeof window !== 'undefined';

// jsdom does not implement these browser APIs, and several Fluent UI v9 components touch them on
// mount (media queries for responsive behaviour, ResizeObserver for overflow/positioning). Without
// the shims those components throw during render and unrelated tests fail with confusing stacks.
if (hasDom && !window.matchMedia) {
  window.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}

if (hasDom && !('ResizeObserver' in window)) {
  class ResizeObserverStub {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  }
  (window as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
}
