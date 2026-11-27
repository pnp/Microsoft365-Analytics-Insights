"""Optional production-bundle check. See the standalone tracker's README for prerequisites."""

import json
import os
import shutil
import subprocess
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit
from uuid import uuid4

from playwright.sync_api import sync_playwright


PROJECT = Path(__file__).resolve().parents[1]
BUNDLE = Path(os.environ.get("TRACKER_TEST_BUNDLE", PROJECT.parent / "aitracker.js"))
LAST_EXIT = "SPOInsightsLastPageStats"
SITE = "/sites/contoso"
PAGE = b"""<!doctype html><meta charset=utf-8><title>Contoso</title>
<a href="#link">Contoso link</a>
<script>
const site = location.origin + '/sites/contoso';
window._spPageContextInfo = {webAbsoluteUrl: site, siteAbsoluteUrl: site, webTitle: 'Contoso',
                             userLoginName: 'reader@contoso.example'};
window._o365AnalyticsInfo = {siteUrls: [site]};
window.appInsightsConnectionStringHash = btoa('InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=' + location.origin);
window.insightsWebRootUrlHash = btoa(location.origin);
localStorage.setItem('AITrackerConfig', JSON.stringify({metadataRefreshMinutes: 60, expiry: '2099-01-01'}));
document.querySelector('a').addEventListener('click', e => e.preventDefault());

// Label the actual request at the receiver; do not replace the transport, SDK, or serialized body.
const send = window.fetch;
window.fetch = function(input, init) {
    if (input instanceof Request && input.url === (window.fixtureReceiver || location.origin) + '/v2/track' && input.keepalive)
        input.headers.set('X-Fixture-Transport', 'keepalive');
    return send.call(this, input, init);
};
const beacon = navigator.sendBeacon.bind(navigator);
navigator.sendBeacon = (url, body) => beacon(url === (window.fixtureReceiver || location.origin) + '/v2/track' ? url + '?beacon' : url, body);
window.loadTracker = async () => {
    const script = document.createElement('script');
    script.src = '/aitracker.js';
    await new Promise((resolve, reject) => {
        script.onload = resolve; script.onerror = reject; document.head.append(script);
    });
    await new Promise(resolve => setTimeout(resolve, 0));
    if (typeof window.modernPageNav !== 'function') throw Error('No initial view');
};
</script>"""


class Fixture(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, content_type, body):
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'; connect-src 'self' "
                         + getattr(self.server, "fixture_receiver", ""))
        if hasattr(self.server, "document_origin"):
            self.send_header("Access-Control-Allow-Origin", self.server.document_origin)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        if not hasattr(self.server, "document_origin") or self.path != "/v2/track" or self.headers.get("Origin") != self.server.document_origin:
            self.send_error(404)
            return
        # Both requests are native. Hold the ordinary JSON POST at its CORS preflight, before
        # any of its body can reach this receiver. Allow the separately labelled keepalive POST.
        if "x-fixture-transport" not in self.headers.get("Access-Control-Request-Headers", "").lower():
            self.server.ordinary_waiting.set()
            if not self.server.release_ordinary.wait(15):
                self.send_error(408)
                return
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", self.server.document_origin)
        self.send_header("Access-Control-Allow-Methods", "POST")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Fixture-Transport")
        self.end_headers()

    def do_GET(self):
        if self.path == "/aitracker.js":
            self.reply("text/javascript", self.server.bundle)
        elif self.path == "/neutral" or self.path.startswith(SITE + "/"):
            self.reply("text/html", PAGE)
        else:
            self.send_error(404)

    def do_POST(self):
        if urlsplit(self.path).path != "/v2/track":
            self.send_error(404)
            return
        items = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        transport = "beacon" if self.path.endswith("?beacon") else self.headers.get("X-Fixture-Transport", "normal")
        if hasattr(self.server, "batches"):
            self.server.batches.append((transport, [i["data"]["baseType"] for i in items]))
        self.server.received.extend((transport, item) for item in items)
        self.reply("application/json", json.dumps({"itemsReceived": len(items), "itemsAccepted": len(items), "errors": []}).encode())


def wait_for(predicate, message, timeout=5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(.02)
    raise AssertionError(message)


class TrackerLifecycle(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        work = PROJECT / "node_modules" / ".cache" / ("tracker-edge-" + uuid4().hex)
        work.mkdir(parents=True)

        def remove_profile():
            try:
                shutil.rmtree(work)
            except (FileNotFoundError, PermissionError):
                return not work.exists()
            return True

        cls.addClassCleanup(wait_for, remove_profile, "Edge did not release its isolated test profile", 30)
        for key in ("TEMP", "TMP", "TMPDIR"):
            previous = os.environ.get(key)
            if previous is None:
                cls.addClassCleanup(os.environ.pop, key, None)
            else:
                cls.addClassCleanup(os.environ.__setitem__, key, previous)
            os.environ[key] = str(work)
        profile = work / "profile"
        edge = Path(os.environ["ProgramFiles(x86)"]) / "Microsoft" / "Edge" / "Application" / "msedge.exe"
        process = subprocess.Popen([
            str(edge), f"--user-data-dir={profile}", "--headless=new", "--no-first-run", "--disable-sync",
            "--disable-background-networking", "--disable-background-mode", "--disable-extensions",
            "--disable-component-update", "--proxy-server=127.0.0.1:9",
            "--proxy-bypass-list=127.0.0.1", "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1",
            "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0", "about:blank",
        ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

        def close_process():
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                process.terminate()
                process.wait(timeout=90)

        cls.addClassCleanup(close_process)
        endpoint = profile / "DevToolsActivePort"

        def read_endpoint():
            try:
                lines = endpoint.read_text().splitlines()
                return lines if len(lines) == 2 else None
            except (FileNotFoundError, PermissionError):
                return None

        port, websocket = wait_for(read_endpoint, "Isolated Edge did not start", 45)
        driver = sync_playwright().start()
        cls.addClassCleanup(driver.stop)
        # Only the port and WebSocket created by this process. Playwright's default focus emulation
        # would otherwise make even a background tab report document.visibilityState === 'visible'.
        browser = driver.chromium.connect_over_cdp(f"ws://127.0.0.1:{port}{websocket}", no_defaults=True, artifacts_dir=work)
        cls.addClassCleanup(browser.close)
        cls.addClassCleanup(browser.new_browser_cdp_session().send, "Browser.close")
        cls.context = browser.contexts[0]
        cls.keeper = cls.context.pages[0]
        print(f"Isolated headless Edge {browser.version}", flush=True)

    def setUp(self):
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Fixture)
        self.addCleanup(self.server.server_close)
        self.server.bundle = BUNDLE.read_bytes()
        self.server.received = []
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.addCleanup(self.server.shutdown)
        self.origin = f"http://127.0.0.1:{self.server.server_port}"
        self.context.clear_cookies()

    def tearDown(self):
        for page in self.context.pages:
            if page != self.keeper:
                page.close()

    def tab(self, path, load=True):
        page = self.context.new_page()
        page.bring_to_front()
        page.goto(self.origin + SITE + path)
        if load:
            page.evaluate("loadTracker()")
        return page

    def items(self, kind, name=None):
        return [(via, e) for via, e in self.server.received if e["data"]["baseType"] == kind
                and (name is None or e["data"]["baseData"]["name"] == name)]

    def cookie(self):
        for c in self.context.cookies(self.origin):
            if c["name"] == LAST_EXIT:
                return json.loads(unquote(c["value"]))
        return None

    def gated_tab(self, path):
        gate = ThreadingHTTPServer(("127.0.0.1", 0), Fixture)
        self.addCleanup(gate.server_close)
        gate.document_origin = self.origin
        gate.received = self.server.received
        gate.batches = []
        gate.ordinary_waiting = threading.Event()
        gate.release_ordinary = threading.Event()
        threading.Thread(target=gate.serve_forever, daemon=True).start()
        self.addCleanup(gate.shutdown)
        self.addCleanup(gate.release_ordinary.set)
        self.server.fixture_receiver = f"http://127.0.0.1:{gate.server_port}"
        page = self.tab(path, load=False)
        page.evaluate("""receiver => {
            window.fixtureReceiver = receiver;
            window.appInsightsConnectionStringHash = btoa(atob(window.appInsightsConnectionStringHash).replace(location.origin, receiver));
        }""", self.server.fixture_receiver)
        return page, gate

    def test_hard_load_leaves_after_50ms(self):
        page, gate = self.gated_tab("/short")
        # Keep the document's native Navigation Timing. Advance the renderer's timers to 50 ms,
        # then close the tab without letting a busy machine run the SDK's 100 ms timer first.
        cdp = self.context.new_cdp_session(page)
        cdp.send("Emulation.setVirtualTimePolicy", {"policy": "pause"})
        page.add_script_tag(url=self.origin + "/aitracker.js")
        expected = page.evaluate("""() => {
            const nav = performance.getEntriesByType('navigation')[0];
            const ms = nav.loadEventEnd - nav.startTime;
            setTimeout(() => { document.querySelector('a').click(); window.clicked = true }, 25);
            return {ms, span: new Date(Math.round(ms)).toISOString().slice(11, 23),
                    time: new Date(performance.timeOrigin + nav.loadEventStart).toISOString()};
        }""")
        exhausted = threading.Event()
        cdp.on("Emulation.virtualTimeBudgetExpired", lambda _: exhausted.set())
        cdp.send("Emulation.setVirtualTimePolicy", {"policy": "advance", "budget": 50})
        wait_for(lambda: page.evaluate("window.clicked") and exhausted.is_set(), "Renderer did not run the 50 ms visit")
        self.assertTrue(gate.ordinary_waiting.wait(5), "Expected the ordinary view request to reach the preflight gate")
        self.assertFalse(self.server.received, "No telemetry POST should have reached the receiver yet")
        with page.expect_event("close", timeout=5000):
            page.close(run_before_unload=True)
        self.assertGreater(expected["ms"], 0)
        wait_for(lambda: self.items("EventData", "LinkClick"), "Short visit's click did not arrive")
        self.assertEqual([kinds for _, kinds in gate.batches], [["PageviewData", "EventData"]])
        (view_transport, view), (click_transport, click) = self.server.received
        self.assertIn(view_transport, ("keepalive", "beacon"))
        self.assertEqual(click_transport, view_transport)
        data = view["data"]["baseData"]
        self.assertEqual(view["time"], expected["time"])
        self.assertEqual(data["duration"], expected["span"])
        self.assertEqual(float(data["properties"]["pageLoad"]), expected["ms"])
        self.assertEqual(click["data"]["baseData"]["properties"]["pageRequestId"], data["properties"]["pageRequestId"])

    def test_acknowledged_view_not_sent_again_on_close(self):
        page, gate = self.gated_tab("/healthy")
        page.evaluate("loadTracker()")
        self.assertTrue(gate.ordinary_waiting.wait(5))
        # A beforeunload prompt can be cancelled, and hiding a tab isn't closing it.
        page.evaluate("dispatchEvent(new Event('beforeunload'))")
        self.keeper.bring_to_front()
        self.assertTrue(page.evaluate("document.hidden"))
        self.assertFalse(self.items("PageviewData"))
        gate.release_ordinary.set()
        wait_for(lambda: self.items("PageviewData"), "Regular view never arrived when the network resumed")
        page.bring_to_front()
        wait_for(lambda: self.items("PageviewPerformanceData"), "Expected the SDK's later performance batch")
        page.wait_for_function("!(JSON.parse(sessionStorage.getItem('AI_sentBuffer_1') || '[]')).some(x => x.bT === 'PageviewData')")
        page.evaluate("document.querySelector('a').click()")
        page.close()
        wait_for(lambda: self.items("EventData", "LinkClick"), "Control click never reached the receiver")
        self.assertEqual([via for via, _ in self.items("PageviewData")], ["normal"])

    def check_two_exits(self, background):
        previous = self.tab("/previous")
        closing = self.tab("/closing")
        wait_for(lambda: len(self.items("PageviewData")) == 2, "Expected two real views")
        ids = {e["data"]["baseData"]["url"]: e["data"]["baseData"]["properties"]["pageRequestId"]
               for _, e in self.items("PageviewData")}
        first, second = ids[previous.url], ids[closing.url]
        self.assertNotEqual(first, second)
        closing.evaluate("""() => {
            history.pushState(null, '', '/sites/untracked/ambient');
            window._o365AnalyticsInfo.siteUrls = [];
            document.querySelector('a').click();
        }""")
        if background:
            self.keeper.bring_to_front()
        previous.goto(self.origin + "/neutral")  # A distinct tab writes its durable exit after the closing tab loaded.
        saved = self.cookie()
        self.assertEqual(saved["pageRequestId"], first)
        self.assertEqual(saved["url"], self.origin + SITE + "/previous")
        self.assertEqual(closing.evaluate("document.hidden"), background)
        closing.close()
        replacement = self.cookie()
        self.assertEqual(replacement["pageRequestId"], second)
        self.assertEqual(replacement["url"], self.origin + SITE + "/closing")
        exits = lambda: self.items("EventData", "PAGE_EXIT")
        delivered = wait_for(exits, "Previous durable exit never reached loopback before another tracker started")
        self.assertEqual(len(delivered), 1)
        via, event = delivered[0]
        self.assertIn(via, ("keepalive", "beacon"))
        self.assertEqual(event["data"]["baseData"]["properties"]["pageRequestId"], first)

        reader = self.tab("/reader")  # A different sender, in a new tab, consumes the closing tab's cookie.
        wait_for(lambda: len(exits()) >= 2, "Closing tab's exit never reached loopback")
        props = [e["data"]["baseData"]["properties"] for _, e in exits()]
        self.assertEqual([(p["pageRequestId"], p["url"], float(p["activeTime"])) for p in props],
                         [(c["pageRequestId"], c["url"], c["secondsOnPage"]) for c in [saved, replacement]])
        self.assertFalse(self.items("EventData", "LinkClick"))
        self.assertCountEqual([e["data"]["baseData"]["url"] for _, e in self.items("PageviewData")], [*ids, reader.url])
        self.assertNotIn("/sites/untracked", json.dumps(self.server.received))

    def test_close_already_hidden(self):
        self.check_two_exits(background=True)

    def test_close_foreground(self):
        self.check_two_exits(background=False)

    def test_back_forward_cache_snapshots(self):
        page = self.tab("/cached")
        cached_url = page.url
        page.evaluate("addEventListener('pageshow', e => { if (e.persisted) window.cacheRestored = true })")
        page.goto(self.origin + SITE + "/between")
        first = self.cookie()
        self.assertEqual(first["url"], cached_url)
        page.evaluate("loadTracker()")
        exits = lambda: [e["data"]["baseData"]["properties"] for _, e in self.items("EventData", "PAGE_EXIT")]
        wait_for(exits, "Intervening document did not deliver the cached visit's snapshot")
        page.go_back(wait_until="commit")
        page.wait_for_function("window.cacheRestored === true", timeout=5000)
        page.goto(self.origin + "/neutral")
        final = self.cookie()
        self.assertEqual(final["pageRequestId"], first["pageRequestId"])
        self.assertGreaterEqual(final["secondsOnPage"], first["secondsOnPage"])
        self.tab("/reader")
        wait_for(lambda: len(exits()) >= 3, "Expected the intervening exit and both cumulative snapshots")
        cached_times = [float(e["activeTime"]) for e in exits() if e["pageRequestId"] == first["pageRequestId"]]
        self.assertEqual(cached_times, [first["secondsOnPage"], final["secondsOnPage"]])
        self.assertEqual(len([e for e in exits() if e["url"] == self.origin + SITE + "/between"]), 1)
        self.assertEqual(len([e for _, e in self.items("PageviewData") if e["data"]["baseData"]["url"] == cached_url]), 1)

    @unittest.skipUnless(os.environ.get("TRACKER_IDLE_WALL_CLOCK") == "1", "opt-in 180 s idle observation")
    def test_idle_wall_clock(self):
        page = self.tab("/clock", load=False)
        self.keeper.bring_to_front()
        self.assertTrue(page.evaluate("document.hidden"))
        page.evaluate("loadTracker()")

        def snapshot():
            # Sample without closing or hiding the document (native tab visibility and real input otherwise).
            page.evaluate("dispatchEvent(new PageTransitionEvent('pagehide'))")
            return self.cookie()

        time.sleep(1)
        self.assertIsNone(snapshot())
        page.bring_to_front()
        page.mouse.move(10, 10)
        active_start = time.monotonic()
        time.sleep(31)
        self.assertFalse(page.evaluate("document.hidden"))
        # Idle detection counts 250 ms checks rather than subtracting wall time. A busy machine
        # can deliver those checks late; first establish the plateau, then measure the ignored time.
        previous = snapshot()["secondsOnPage"]
        while time.monotonic() - active_start < 90:
            time.sleep(1)
            stopped = snapshot()["secondsOnPage"]
            if stopped == previous:
                break
            previous = stopped
        else:
            self.fail("The active clock did not stop")
        self.assertGreaterEqual(stopped, 29)
        time.sleep(180)
        self.assertEqual(snapshot()["secondsOnPage"], stopped)
        page.mouse.move(60, 20)
        time.sleep(1)
        self.assertGreater(snapshot()["secondsOnPage"], stopped + .5)


if __name__ == "__main__":
    unittest.main(verbosity=2)
