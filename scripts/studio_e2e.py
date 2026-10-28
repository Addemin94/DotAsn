#!/usr/bin/env python3
"""End-to-end check of a published DotAsn Studio site in headless Chromium (Playwright).

The site is served under a base path the way GitHub Pages serves a project site (unknown paths get 404.html), then
every tab is exercised with known answers. Any browser console error or page error fails the check.

usage:
    scripts/publish-studio.sh /DotAsn/
    .venv/bin/pip install playwright && .venv/bin/playwright install chromium
    .venv/bin/python scripts/studio_e2e.py artifacts/studio/publish/wwwroot --base /DotAsn/ [--screenshot studio.png]
"""
import argparse
import functools
import http.server
import os
import sys
import threading

from playwright.sync_api import expect, sync_playwright


class PagesHandler(http.server.SimpleHTTPRequestHandler):
    """Serves the site below a base path; like GitHub Pages, missing files get 404.html with status 404."""

    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map,
                      '.wasm': 'application/wasm', '.js': 'text/javascript', '.json': 'application/json'}

    def __init__(self, *args, base, **kwargs):
        self.base = base
        super().__init__(*args, **kwargs)

    def translate_path(self, path):
        path = path.split('?', 1)[0].split('#', 1)[0]
        if not path.startswith(self.base):
            return os.path.join(self.directory, '__missing__')
        return super().translate_path('/' + path[len(self.base):])

    def send_head(self):
        target = self.translate_path(self.path)
        if os.path.isdir(target):
            target = os.path.join(target, 'index.html')
        if not os.path.exists(target):
            self.send_response(404)
            body = open(os.path.join(self.directory, '404.html'), 'rb').read()
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return None
        return super().send_head()

    def log_message(self, *args):
        pass


def output(page, title):
    return page.locator('.output', has=page.locator('.output-head span', has_text=title)).locator('pre').first


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('site')
    ap.add_argument('--base', default='/')
    ap.add_argument('--screenshot')
    args = ap.parse_args()
    base = args.base if args.base.endswith('/') else args.base + '/'

    handler = functools.partial(PagesHandler, directory=os.path.abspath(args.site), base=base)
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f'http://127.0.0.1:{server.server_address[1]}{base}'

    problems = []
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={'width': 1400, 'height': 1000})
        page.on('console', lambda m: problems.append(f'console {m.type}: {m.text}') if m.type == 'error' and 'Failed to load resource' not in m.text else None)
        page.on('pageerror', lambda e: problems.append(f'page error: {e}'))
        # The deliberate unknown-path request below is the only expected 404.
        page.on('response', lambda r: problems.append(f'HTTP {r.status}: {r.url}') if r.status >= 400 and 'no/such/page' not in r.url else None)
        expect.set_options(timeout=60_000)

        def step(name):
            print(f'  {name}')

        page.goto(url)
        tab = lambda name: page.locator('nav.tabs button', has_text=name).first
        primary = lambda name: page.locator('section button.primary', has_text=name).first
        # The schema and value editors are CodeMirror (contenteditable); the decode and inspect inputs are textareas.
        schema_editor = page.locator('.code-editor[data-language="asn1"] .cm-content')
        value_editor = page.locator('.code-editor[data-language="json"] .cm-content')

        step('boots under the base path and compiles the default sample')
        expect(page.locator('.status')).to_contain_text('type(s) ready')

        step('highlights the schema with the DotAsn lexer')
        expect(page.locator('.code-editor .dotasn-keyword', has_text='DEFINITIONS').first).to_be_visible()
        expect(page.locator('.code-editor .dotasn-builtintype', has_text='SEQUENCE').first).to_be_visible()
        expect(page.locator('.code-editor .dotasn-typereference', has_text='Heartbeat').first).to_be_visible()

        step('encodes a Heartbeat with UPER (X.691 known answer 07EE6B2800)')
        tab('Encode').click()
        value_editor.fill('{"sequenceNumber": 7, "uptimeSeconds": 4000000000}')
        primary('Encode').click()
        expect(output(page, 'UPER hex')).to_have_text('07 EE 6B 28 00')
        expect(page.locator('section')).to_contain_text('40 significant bit(s)')

        step('validates constraints of nested components')
        value_editor.fill('{"sequenceNumber": 300, "uptimeSeconds": 4000000000}')
        page.get_by_role('button', name='Validate').click()
        expect(output(page, 'Validation')).to_contain_text('sequenceNumber')
        value_editor.fill('{"sequenceNumber": 7, "uptimeSeconds": 4000000000}')
        page.get_by_role('button', name='Validate').click()
        expect(output(page, 'Validation')).to_contain_text('satisfies all constraints')

        step('compares the value across all ten encoding rules')
        page.get_by_role('button', name='Compare all rules').click()
        expect(page.locator('table.compare tbody tr')).to_have_count(10)

        step('sends the encoding to the decoder and transcodes it to JER')
        page.get_by_role('button', name='Send to Decode').click()
        expect(output(page, 'Value')).to_contain_text('"uptimeSeconds": 4000000000')
        expect(page.locator('section')).to_contain_text('5 of 5 octet(s) consumed')
        page.get_by_role('button', name='Transcode', exact=True).click()
        expect(output(page, 'JER')).to_contain_text('"uptimeSeconds":4000000000')

        step('guesses the types a message could be')
        page.get_by_role('button', name='Guess type').click()
        guesses = page.locator('table.guesses tbody tr')
        expect(guesses.filter(has_text='Telemetry-Protocol.Heartbeat')).to_contain_text('exact')
        expect(guesses.filter(has_text='Telemetry-Protocol.TimeSyncRequest')).to_contain_text('exact')
        expect(page.locator('section')).to_contain_text('fit exactly')
        guesses.filter(has_text='Telemetry-Protocol.TimeSyncRequest').get_by_role('button', name='Decode as').click()
        expect(output(page, 'Value')).to_contain_text('"deviceTime": 4000000000')
        expect(page.locator('table.guesses tr.selected')).to_contain_text('TimeSyncRequest')
        guesses.filter(has_text='Telemetry-Protocol.Heartbeat').get_by_role('button', name='Decode as').click()
        expect(output(page, 'Value')).to_contain_text('"uptimeSeconds": 4000000000')

        step('reports a malformed message instead of failing')
        page.locator('textarea.editor').fill('07')
        primary('Decode').click()
        expect(output(page, 'Decoding failed')).to_be_visible()

        step('inspects DER TLVs (schema-less)')
        tab('Encode').click()
        page.locator('section select[title="Encoding rule"]').first.select_option('DER')
        value_editor.fill('{"sequenceNumber": 7, "uptimeSeconds": 4000000000}')
        primary('Encode').click()
        expect(output(page, 'DER hex')).to_have_text('30 0A 80 01 07 81 05 00 EE 6B 28 00')
        page.get_by_role('button', name='Inspect TLVs').click()
        expect(output(page, 'TLV structure')).not_to_be_empty()
        expect(output(page, 'TLV structure')).not_to_contain_text('error')

        step('generates highlighted C# in the prefilled namespace (lazy-loads Roslyn)')
        tab('C#').click()
        expect(page.get_by_label('Namespace')).to_have_value('Protocol.Test')
        primary('Generate').click()
        csharp = page.locator('.code-editor[data-language="csharp"] .cm-content')
        expect(csharp).to_contain_text('namespace Protocol.Test;', timeout=120_000)
        expect(page.locator('.code-editor[data-language="csharp"] .dotasn-keyword', has_text='namespace').first).to_be_visible()
        expect(page.locator('.code-editor[data-language="csharp"] .dotasn-comment').first).to_be_visible()
        # Read-only: selectable and copyable, but typing changes nothing.
        expect(csharp).to_have_attribute('aria-readonly', 'true')
        csharp.click()
        page.keyboard.type('xyz')
        expect(csharp).not_to_contain_text('xyz')

        step('generates TypeScript and the schema (lazy-loads the generator)')
        tab('TypeScript').click()
        primary('Generate').click()
        expect(output(page, 'TelemetryProtocol.ts')).to_contain_text('export interface Heartbeat', timeout=60_000)
        expect(output(page, 'dotasn-schema.ts')).to_contain_text('export const schema')
        with page.expect_download() as download:
            page.get_by_role('button', name='Download schema.json').click()
        assert download.value.suggested_filename == 'schema.json'
        assert '"format": "dotasn-schema"' in open(download.value.path(), encoding='utf-8').read()

        step('shows the resolved model')
        tab('Model').click()
        expect(output(page, 'Model')).to_contain_text('Heartbeat')

        step('reports compile errors with locations')
        tab('Schema').click()
        schema_editor.fill('M DEFINITIONS ::= BEGIN\n  A ::= SEQUENCE { a Missing }\nEND')
        primary('Compile').click()
        expect(page.locator('.status')).to_contain_text('error(s)')
        expect(page.locator('table.diagnostics .badge.error').first).to_be_visible()
        # Inline: the unresolved reference is underlined and explained on hover.
        expect(page.locator('.cm-lintRange-error', has_text='Missing')).to_be_visible()
        page.locator('.cm-lintRange-error', has_text='Missing').hover()
        expect(page.locator('.cm-tooltip-lint')).to_contain_text('Missing')
        # Clicking a diagnostic moves the cursor to its line.
        page.locator('table.diagnostics tbody tr').first.click()
        expect(page.locator('.cm-activeLine')).to_contain_text('A ::= SEQUENCE')

        step('loads a sample from the samples directory')
        page.locator('select[title="Load a sample specification"]').select_option('extensibility/extensibility.asn')
        page.get_by_role('button', name='Load', exact=True).click()
        expect(page.locator('.status')).to_contain_text('type(s) ready')

        step('serves 404.html for unknown paths (GitHub Pages behaviour) and still boots')
        page.goto(url + 'no/such/page')
        expect(page.locator('main')).to_contain_text('Nothing here')

        if args.screenshot:
            page.goto(url)
            expect(page.locator('.status')).to_contain_text('type(s) ready')
            tab('Encode').click()
            page.get_by_role('button', name='Compare all rules').click()
            expect(page.locator('table.compare tbody tr')).to_have_count(10)
            page.screenshot(path=args.screenshot, full_page=True)
        browser.close()

    server.shutdown()
    if problems:
        print('FAILED: browser reported errors:\n  ' + '\n  '.join(problems))
        return 1
    print('PASSED')
    return 0


if __name__ == '__main__':
    sys.exit(main())
