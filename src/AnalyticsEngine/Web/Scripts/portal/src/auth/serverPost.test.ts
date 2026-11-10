import { describe, it, expect, vi, afterEach } from 'vitest';

import { postToServer } from './serverPost';

afterEach(() => {
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

describe('postToServer', () => {
  it('submits an empty POST form to the path, never a GET navigation', () => {
    const submitted: { method: string; action: string }[] = [];
    vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(function (this: HTMLFormElement) {
      submitted.push({ method: this.method, action: this.getAttribute('action') ?? '' });
    });

    postToServer('/Account/SignOut');

    expect(submitted).toEqual([{ method: 'post', action: '/Account/SignOut' }]);
    expect(document.querySelectorAll('form input')).toHaveLength(0);
  });
});
