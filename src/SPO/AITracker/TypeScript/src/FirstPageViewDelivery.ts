/// <reference lib="es2021.weakref" />
import type { Sender } from '@microsoft/applicationinsights-channel-js';
import { isBeaconsSupported, PageViewDataType } from '@microsoft/applicationinsights-core-js';

type SendBuffer = Sender['_buffer'];
type BufferedItem = ReturnType<SendBuffer['getItems']>[number];

// Leave room in the browser's shared 64 KiB keepalive quota for other unload work.
const MAX_VIEW_BYTES = 16 * 1024;
const MAX_CLOSE_BATCH_BYTES = 48 * 1024;

function fits(text: string, limit: number): boolean {
    if (text.length > limit) return false;
    if (text.length * 3 <= limit) return true; // UTF-8 needs at most three bytes per UTF-16 code unit.
    try { return new Blob([text]).size <= limit; }
    catch { return false; }
}

export class FirstPageViewDelivery {
    private inFlight?: { buffer: SendBuffer, item: () => BufferedItem | undefined, release: () => void };

    get awaitingResponse(): boolean { return !!this.inFlight?.item(); }

    track(sender: Sender, track: () => void): void {
        // In 3.4.6 Analytics immediately flushes a measured view. Observe only THIS sender's
        // synchronous handoff, after sampling, privacy processing and envelope serialization.
        const buffer = sender._buffer;
        // With no Beacon support this version falls back to ordinary XHR even on unload.
        if (!isBeaconsSupported() || typeof Blob !== 'function' || !buffer) return track();
        const enqueue = buffer.enqueue;
        buffer.enqueue = view => {
            const count = buffer.count();
            enqueue.call(buffer, view);
            if (view.bT !== PageViewDataType || buffer.count() === count || !fits(view.item, MAX_VIEW_BYTES)) return;

            // The pending XHR already owns this record. Don't extend its lifetime; on older
            // browsers without WeakRef, bound our one reference to a minute.
            const ref = typeof WeakRef === 'function' ? new WeakRef(view) : undefined;
            let retained = ref ? undefined : view;
            const item = () => ref ? ref.deref() : retained;
            const clear = buffer.clearSent;
            const release = () => {
                window.clearTimeout(expiry);
                retained = undefined;
                buffer.clearSent = clear;
                this.inFlight = undefined;
            };
            const expiry = ref ? undefined : window.setTimeout(release, 60_000);
            buffer.clearSent = settled => {
                clear.call(buffer, settled);
                // The SDK also clears a rejected item or moves it back to its own retry queue.
                const current = item();
                if (!current || settled.indexOf(current) !== -1) release();
            };
            this.inFlight = { buffer, item, release };
        };
        try { track(); }
        finally { buffer.enqueue = enqueue; }
    }

    flushOnClose(sender: Sender): void {
        const flight = this.inFlight;
        if (!flight) return sender.onunloadFlush();
        const item = flight.item();
        flight.release();
        const buffer = flight.buffer;
        if (!item || buffer !== sender._buffer || item.item.length + buffer.size() > MAX_CLOSE_BATCH_BYTES)
            return sender.onunloadFlush();

        // The ordinary request has not settled. Offer the ORIGINAL envelope ahead of the
        // queued clicks, without re-running analytics against the page that's visible now.
        const queued = buffer.getItems();
        if (queued.indexOf(item) !== -1) return sender.onunloadFlush();
        const batch = [item, ...queued];
        if (!fits(buffer.batchPayloads(batch), MAX_CLOSE_BATCH_BYTES))
            return sender.onunloadFlush();
        const { getItems, count } = buffer;
        buffer.getItems = () => batch;
        buffer.count = () => batch.length;
        try { sender.onunloadFlush(); }
        finally { buffer.getItems = getItems; buffer.count = count; }
    }
}
