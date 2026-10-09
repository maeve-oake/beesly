"""Run after dotnet build: python3 tests/touch_ui.py (with dotnet on PATH).
Uses a local HA stub, never the configured HA server or credentials.
"""
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

root = Path(__file__).resolve().parents[1]
views = json.loads((root / 'views.example.json').read_text())
views['independent'] = {'title': 'Independent targets', 'type': 'multiple',
    'entities': ['switch.one', 'switch.two', 'switch.three', 'switch.four'],
    'swatch': {'enable': True, 'entities': ['light.hidden']}}
views['plain'] = {'title': 'No swatch', 'type': 'lamp', 'entity': 'light.plain'}
ids = ['light.living_room_floor_lamp', 'climate.aircon', 'light.desk_led', 'light.rack_led',
       'light.maeve_room_ceiling_light', 'light.hidden', 'light.plain',
       'switch.one', 'switch.two', 'switch.three', 'switch.four']
states = {id: {'entity_id': id, 'state': 'off', 'attributes': {'friendly_name': id,
    'temperature': 24, 'current_temperature': 22, 'min_temp': 16, 'max_temp': 30}} for id in ids}
calls = []

class HA(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def reply(self, data):
        body = json.dumps(data).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
    def do_GET(self): self.reply(list(states.values()))
    def do_POST(self):
        data = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        calls.append((self.path, data))
        ids = data['entity_id']
        for id in [ids] if isinstance(ids, str) else ids:
            if self.path.endswith('/toggle'): states[id]['state'] = 'on' if states[id]['state'] == 'off' else 'off'
            if 'hvac_mode' in data: states[id]['state'] = data['hvac_mode']
            if 'temperature' in data: states[id]['attributes']['temperature'] = data['temperature']
        self.reply([])

ha = ThreadingHTTPServer(('127.0.0.1', 0), HA)
threading.Thread(target=ha.serve_forever, daemon=True).start()
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
env = {k: v for k, v in os.environ.items() if not k.startswith(('HA_', 'AMI_', 'UI_VIEWS'))}
env.update(HA_URL=f'http://127.0.0.1:{ha.server_port}', HA_TOKEN='test', HA_ENTITIES='[]',
           AMI_SLOTS='{"05":"switch.one"}', UI_VIEWS=json.dumps(views), PORT=str(port), LISTEN_ADDRESS='127.0.0.1')
base = f'http://127.0.0.1:{port}'
def request(path, method='GET', status=200):
    try:
        with urllib.request.urlopen(urllib.request.Request(path if path.startswith('http') else base + path, method=method)) as r:
            assert r.status == status
            return r.read()
    except urllib.error.HTTPError as e:
        assert e.code == status, (path, e.code, e.read())
        return e.read()
def screen(view, suffix=''):
    return ET.fromstring(request('/ha/touch.xml?view=' + view + suffix))
def image(screen):
    png = request(screen.findtext('URL'))
    assert png[:8] == b'\x89PNG\r\n\x1a\n'
    assert struct.unpack('>II', png[16:24]) == (298, 168)
    return png

with tempfile.TemporaryDirectory() as directory, tempfile.TemporaryFile(mode='w+') as log:
    config_file = Path(directory) / 'views.json'
    config_file.write_text(env.pop('UI_VIEWS'))
    env['UI_VIEWS_FILE'] = str(config_file)
    command = [os.environ['BEESLY_EXECUTABLE']] if 'BEESLY_EXECUTABLE' in os.environ else [os.environ.get('DOTNET', 'dotnet'), str(root / 'bin/Debug/net10.0/beesly.dll')]
    process = subprocess.Popen(command, cwd=directory, env=env, stdout=log, stderr=log)
    try:
        for _ in range(100):
            try: request('/app.xml'); break
            except urllib.error.URLError: time.sleep(.1)
        else: raise AssertionError('Beesly did not start')
        menu = request('/app.xml').decode()
        assert 'view=maeve-bedroom' in menu
        assert 'No entities selected' in request('/ha.xml').decode()
        view_menu = ET.fromstring(request('/ha/views.xml'))
        assert view_menu.tag == 'CiscoIPPhoneMenu'
        assert [(item.findtext('Name'), item.findtext('URL')) for item in view_menu.findall('MenuItem')] == [
            (view['title'], base + '/ha/touch.xml?view=' + id + '&page=0') for id, view in views.items()]
        assert view_menu.find('SoftKeyItem/URL').text == 'SoftKey:Select'
        assert not calls, 'Opening the view menu must not control any entities'
        for view in views:
            pane = screen(view)
            assert pane.findtext('Title') == views[view]['title']
            areas = []
            for item in pane.findall('MenuItem'):
                area = item.find('TouchArea')
                bounds = tuple(int(area.get(axis)) for axis in ['X1', 'Y1', 'X2', 'Y2'])
                x1, y1, x2, y2 = bounds
                assert 0 <= x1 < x2 < 298 and 0 <= y1 < y2 < 168, bounds
                for left, top, right, bottom in areas:
                    assert x2 < left or right < x1 or y2 < top or bottom < y1, (bounds, areas)
                areas.append(bounds)
            png = image(pane)
            assert image(screen(view)) == png
            if view in ('floor-lamp', 'aircon', 'maeve-bedroom'):
                Path(tempfile.gettempdir(), 'beesly-' + view + '.png').write_bytes(png)
        # Exercise native-resolution rendering in every power state; optionally retain previews.
        preview_dir = Path(os.environ['BEESLY_PREVIEW_DIR']) if 'BEESLY_PREVIEW_DIR' in os.environ else None
        if preview_dir: preview_dir.mkdir(parents=True, exist_ok=True)
        for view, entity in [('floor-lamp', 'light.living_room_floor_lamp'), ('aircon', 'climate.aircon')]:
            urls = set()
            for state in ['on', 'off', 'unavailable']:
                states[entity]['state'] = 'cool' if view == 'aircon' and state == 'on' else state
                pane = screen(view)
                urls.add(pane.findtext('URL'))
                png = image(pane)
                close = next(m for m in pane.findall('MenuItem') if m.findtext('Name') == 'Close')
                assert close.findtext('URL') == 'Init:Services'
                if preview_dir: (preview_dir / f'{view}-{state}.png').write_bytes(png)
            assert len(urls) == 3
            states[entity]['state'] = 'off'
        for entity, state in [('light.desk_led', 'on'), ('light.rack_led', 'off'), ('light.maeve_room_ceiling_light', 'unavailable')]:
            states[entity]['state'] = state
        png = image(screen('maeve-bedroom'))
        if preview_dir: (preview_dir / 'maeve-bedroom.png').write_bytes(png)
        for entity in ['light.desk_led', 'light.rack_led', 'light.maeve_room_ceiling_light']:
            states[entity]['state'] = 'off'
        if preview_dir:
            (preview_dir / 'multiple-page-2.png').write_bytes(image(screen('independent', '&page=1')))
            (preview_dir / 'lamp-no-swatch.png').write_bytes(image(screen('plain')))
        first = screen('independent')
        assert any('page=1' in m.findtext('URL') for m in first.findall('MenuItem'))
        second = screen('independent', '&page=1')
        assert any('switch.four' in m.findtext('URL') for m in second.findall('MenuItem'))
        screen('floor-lamp', '&toggle=light.living_room_floor_lamp')
        assert calls[-1][0] == '/api/services/light/toggle'
        screen('maeve-bedroom', '&color=purple')
        assert calls[-1][1]['entity_id'] == ['light.desk_led', 'light.rack_led']
        screen('independent', '&color=red')
        assert calls[-1][1]['entity_id'] == ['light.hidden']
        # Old cached screens retain their one-shot brightness action.
        request('/touch-ui/brightness/dim?view=maeve-bedroom', method='POST')
        assert calls[-1][1]['entity_id'] == ['light.desk_led', 'light.rack_led']
        assert calls[-1][1]['brightness_step_pct'] == -20

        def notify(uri):
            protocol, transport, host, notify_port, path, credentials, data = uri.split(':', 6)
            assert (protocol, transport, host, notify_port, credentials, data) == ('Notify', 'http', '127.0.0.1', str(port), '', '')
            return request('/' + path, method='POST')

        def wait_for(predicate, timeout=3):
            deadline = time.monotonic() + timeout
            while not predicate():
                assert time.monotonic() < deadline, 'Timed out waiting for hold action'
                time.sleep(.02)

        held_screen = screen('maeve-bedroom')
        keys = {k.findtext('Name'): k for k in held_screen.findall('SoftKeyItem')}
        dim, brighter = keys['Dim'], keys['Brighter']
        assert 'phase=press' in dim.findtext('URLDown')
        assert 'phase=release' in dim.findtext('URL')
        assert 'session=' in dim.findtext('URLDown')
        count = len(calls)
        notify(dim.findtext('URL'))  # A duplicate or unmatched release is harmless.
        assert len(calls) == count
        notify(dim.findtext('URLDown'))
        wait_for(lambda: len(calls) > count)
        assert calls[-1][1]['entity_id'] == ['light.desk_led', 'light.rack_led']
        assert calls[-1][1]['brightness_step_pct'] == -5
        # A retry must neither produce an immediate extra step nor create a second loop.
        notify(dim.findtext('URLDown'))
        time.sleep(.1)
        assert len(calls) == count + 1
        wait_for(lambda: len(calls) >= count + 3)
        notify(dim.findtext('URL'))
        time.sleep(.1)
        stopped = len(calls)
        time.sleep(.45)
        assert len(calls) == stopped
        notify(brighter.findtext('URLDown'))
        wait_for(lambda: len(calls) > stopped)
        assert calls[-1][1]['brightness_step_pct'] == 5
        notify(dim.findtext('URL'))  # An old opposite-direction release must not stop this hold.
        current = len(calls)
        wait_for(lambda: len(calls) > current)
        # Two screen sessions are independent, even when their Notify POSTs share an IP.
        other_screen = screen('independent')
        other_dim = next(k for k in other_screen.findall('SoftKeyItem') if k.findtext('Name') == 'Dim')
        notify(other_dim.findtext('URLDown'))
        wait_for(lambda: any(data.get('entity_id') == ['light.hidden'] for _, data in calls[current:]))
        notify(held_screen.get('onAppFocusLost'))
        time.sleep(.1)
        current = len(calls)
        wait_for(lambda: len(calls) > current)
        assert all(data['entity_id'] == ['light.hidden'] for _, data in calls[current:])
        notify(other_screen.get('onAppClosed'))
        time.sleep(.1)
        stopped = len(calls)
        time.sleep(.45)
        assert len(calls) == stopped
        # Missing release: the server must stop itself after the fixed ten-second deadline.
        notify(brighter.findtext('URLDown'))
        wait_for(lambda: len(calls) > stopped)
        time.sleep(10.3)
        stopped = len(calls)
        time.sleep(.45)
        assert len(calls) == stopped
        request('/touch-ui/brightness/dim?view=maeve-bedroom&phase=press', method='POST', status=400)
        request('/touch-ui/brightness/stop?view=maeve-bedroom', method='POST', status=400)
        print('PASS: brightness press/repeat/release, duplicate presses, independent sessions, focus/close cancellation and lost-release timeout')
        screen('aircon', '&step=-1')
        assert calls[-1][1]['temperature'] == 23
        screen('aircon', '&toggle=climate.aircon')
        assert calls[-1][1]['hvac_mode'] == 'cool'
        count = len(calls)
        for path in ['view=maeve-bedroom&toggle=light.hidden', 'view=plain&color=red',
                     'view=aircon&temperature=100', 'view=aircon&step=2',
                     'view=aircon&toggle=climate.aircon&step=1', 'view=plain&temperature=22']:
            request('/ha/touch.xml?' + path, status=400)
        request('/ha/touch.xml?view=missing', status=404)
        request('/touch-ui/brightness/dim?view=missing', method='POST', status=404)
        assert len(calls) == count
        states['light.living_room_floor_lamp']['state'] = 'unavailable'
        pane = screen('floor-lamp', '&toggle=light.living_room_floor_lamp')
        assert len(calls) == count
        assert not any('toggle=' in m.findtext('URL') for m in pane.findall('MenuItem'))
        image(pane)
        print('PASS: independent menu/AMI/views, PNGs, caching, pagination, toggles, swatch targets, brightness, climate and action validation')
    except BaseException:
        log.seek(0); print(log.read()); raise
    finally:
        process.terminate(); process.wait(timeout=10); ha.shutdown()

    invalid = [
        {'title': 'Wrong type', 'type': 'bedroom', 'entities': ['light.a']},
        {'title': 'Empty', 'type': 'multiple', 'entities': []},
        {'title': 'Wrong domain', 'type': 'lamp', 'entity': 'switch.a'},
        {'title': 'Ambiguous', 'type': 'lamp', 'entity': 'light.a', 'entities': ['light.b']},
        {'title': 'Duplicate', 'type': 'multiple', 'entities': ['light.a', 'light.a']},
        {'title': 'No targets', 'type': 'lamp', 'entity': 'light.a', 'swatch': {'enable': True}},
        {'title': 'Bad target', 'type': 'lamp', 'entity': 'light.a', 'swatch': {'enable': True, 'entities': ['switch.a']}},
        {'title': 'Disabled', 'type': 'lamp', 'entity': {'entityId': 'light.a', 'allowToggle': False}},
    ]
    for view in invalid:
        config_file.write_text(json.dumps({'invalid': view}))
        result = subprocess.run(command, cwd=directory, env=env, capture_output=True, timeout=10)
        assert result.returncode != 0, view
        assert b"UI view 'invalid':" in result.stderr, result.stderr
    print('PASS: invalid view configurations rejected at startup')
