/** @jest-environment jsdom */
import { AnalyticsPlugin } from '@microsoft/applicationinsights-analytics-js';
import { Sender } from '@microsoft/applicationinsights-channel-js';
import { AppInsightsCore, isBeaconsSupported } from '@microsoft/applicationinsights-core-js';
import { FirstPageViewDelivery } from '../src/FirstPageViewDelivery';

const url = 'http://127.0.0.1:9/v2/track';
const key = '00000000-0000-0000-0000-000000000000';

function fixture() {
    const requests: { xhr: XMLHttpRequest, body: string }[] = [];
    jest.spyOn(XMLHttpRequest.prototype, 'send').mockImplementation(function (this: XMLHttpRequest, body) {
        if (typeof body !== 'string') throw new Error('Expected a serialized SDK batch');
        requests.push({ xhr: this, body });
    });
    const beacons: Blob[] = [];
    Object.defineProperty(navigator, 'sendBeacon', { configurable: true, value: (target: string, body: Blob) => {
        if (target !== url) throw new Error('Unexpected fixture destination');
        beacons.push(body);
        return true;
    } });
    isBeaconsSupported(false);
    const core = new AppInsightsCore(), analytics = new AnalyticsPlugin(), sender = new Sender();
    const config = { instrumentationKey: key, endpointUrl: url, disableExceptionTracking: true,
        enableSessionStorageBuffer: false, isBeaconApiDisabled: true };
    core.initialize(config, [analytics, sender]);
    const delivery = new FirstPageViewDelivery();
    const track = (extra = '') => delivery.track(sender, () => analytics.trackPageView({ name: 'Contoso', uri: 'https://contoso.example/',
        properties: { duration: 123, pageRequestId: 'synthetic-view', extra } }));
    const click = (extra = '') => analytics.trackEvent({ name: 'LinkClick',
        properties: { pageRequestId: 'synthetic-view', a: extra, b: extra, c: extra } });
    const settle = (status: number) => {
        const { xhr } = requests[0];
        Object.defineProperties(xhr, { readyState: { value: 4 }, status: { value: status }, responseText: { value: '' } });
        xhr.onreadystatechange!.call(xhr, new Event('readystatechange'));
    };
    const flush = () => delivery.flushOnClose(sender);
    return { requests, beacons, track, click, settle, flush, core };
}

async function items(blob: Blob) {
    const text = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result as string);
        reader.onerror = reject;
        reader.readAsText(blob);
    });
    return JSON.parse(text);
}

afterEach(() => {
    jest.restoreAllMocks();
    Reflect.deleteProperty(navigator, 'sendBeacon');
    isBeaconsSupported(false);
});

test.each(['in flight', '200', '403', '503', 'large view', 'large unload batch'])('closes with only the SDK-eligible envelopes (ordinary attempt: %s)', async scenario => {
    const f = fixture();
    try {
        f.track(scenario === 'large view' ? '界'.repeat(7000) : '');
        f.click(scenario === 'large unload batch' ? '界'.repeat(6000) : '');
        expect(f.requests).toHaveLength(1);
        const original = JSON.parse(f.requests[0].body)[0];
        if (scenario === '200' || scenario === '403' || scenario === '503') f.settle(Number(scenario));
        f.flush();
        f.flush();
        expect(f.beacons).toHaveLength(1);
        const batch = await items(f.beacons[0]);
        const views = batch.filter(e => e.data.baseType === 'PageviewData');
        // On 503 the SDK requeued its own copy. On 403 it discarded it. Neither needs a rescue.
        expect(views).toEqual(scenario === 'in flight' || scenario === '503' ? [original] : []);
        expect(batch.filter(e => e.data.baseData.name === 'LinkClick')).toHaveLength(1);
        if (scenario === 'in flight') expect(batch[0]).toEqual(original);
    } finally {
        f.core.unload(false);
    }
});

test('a browser without WeakRef stops holding an unacknowledged view after the short visit window', async () => {
    const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'WeakRef');
    const f = fixture();
    try {
        Object.defineProperty(globalThis, 'WeakRef', { configurable: true, value: undefined });
        jest.useFakeTimers();
        f.track();
        expect(f.requests).toHaveLength(1);
        jest.advanceTimersByTime(61_000);
        jest.useRealTimers();
        f.click();
        f.flush();
        expect(f.beacons).toHaveLength(1);
        expect((await items(f.beacons[0])).map(e => e.data.baseData.name)).toEqual(['LinkClick']);
    } finally {
        if (descriptor) Object.defineProperty(globalThis, 'WeakRef', descriptor);
        else Reflect.deleteProperty(globalThis, 'WeakRef');
        jest.useRealTimers();
        f.core.unload(false);
    }
});
