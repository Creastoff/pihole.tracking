import assert from 'node:assert/strict';
import fs from 'node:fs';
import nodePath from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const repositoryRoot = nodePath.resolve(nodePath.dirname(fileURLToPath(import.meta.url)), '../..');
const appSource = fs.readFileSync(nodePath.join(repositoryRoot, 'wwwroot', 'app.js'), 'utf8');

function loadApp() {
  const context = {
    clearTimeout,
    console,
    document: { addEventListener() {} },
    setTimeout,
    URL,
    URLSearchParams
  };
  vm.createContext(context);
  vm.runInContext(`${appSource}\nthis.__testApi = { state, filteredDomains, pendingReviewDomains, domainsWithBlockListEntries, isPiholeBlocked, clientIpValues, compactNumber, escapeHtml, truncateText };`, context);
  return context.__testApi;
}

function domain(name, overrides = {}) {
  return {
    domain: name,
    queryCount: 1,
    blockedCount: 0,
    firstSeen: null,
    lastSeen: null,
    clients: [],
    clientIps: [],
    primaryStatus: 'FORWARDED',
    ...overrides
  };
}

test('needs-review hides reviewed and Pi-hole-blocked domains by default', () => {
  const { state, filteredDomains, pendingReviewDomains } = loadApp();
  state.domains = [
    domain('new.example'),
    domain('known.example'),
    domain('local-block.example'),
    domain('investigate.example'),
    domain('gravity.example', { primaryStatus: 'GRAVITY' }),
    domain('blocked.example', { blockedCount: 1 })
  ];
  state.known = new Set(['known.example']);
  state.blockList = new Set(['local-block.example']);
  state.investigations = new Map([['investigate.example', { domain: 'investigate.example' }]]);
  state.filter = 'needs-review';
  state.hidePiholeBlocked = true;
  state.ipFilter = 'all';
  state.search = '';

  assert.deepEqual(Array.from(filteredDomains(), (item) => item.domain), ['new.example']);
  assert.deepEqual(Array.from(pendingReviewDomains(), (item) => item.domain), ['new.example']);
});

test('disabling the Pi-hole filter reveals blocked query-log domains', () => {
  const { state, filteredDomains } = loadApp();
  state.domains = [
    domain('gravity.example', { primaryStatus: 'GRAVITY' }),
    domain('blocked.example', { blockedCount: 1 }),
    domain('new.example')
  ];
  state.known = new Set();
  state.blockList = new Set();
  state.investigations = new Map();
  state.filter = 'all';
  state.hidePiholeBlocked = false;
  state.ipFilter = 'all';
  state.search = '';

  assert.deepEqual(Array.from(filteredDomains(), (item) => item.domain), [
    'gravity.example',
    'blocked.example',
    'new.example'
  ]);
});

test('blocked filter includes local-only block-list entries', () => {
  const { state, filteredDomains } = loadApp();
  state.domains = [domain('query-log.example'), domain('blocked.example')];
  state.known = new Set();
  state.blockList = new Set(['blocked.example', 'local-only.example']);
  state.investigations = new Map();
  state.filter = 'blocked';
  state.hidePiholeBlocked = true;
  state.ipFilter = 'all';
  state.search = '';

  const results = Array.from(filteredDomains());
  assert.deepEqual(results.map((item) => item.domain), ['blocked.example', 'local-only.example']);
  assert.equal(results.find((item) => item.domain === 'local-only.example').blockListOnly, true);
});

test('IP filtering uses client IPs and falls back to client values', () => {
  const { state, filteredDomains, clientIpValues } = loadApp();
  const withIp = domain('with-ip.example', { clientIps: ['192.168.1.10'], clients: ['Laptop'] });
  const withFallback = domain('fallback.example', { clients: ['192.168.1.10'] });
  state.domains = [withIp, withFallback, domain('other.example', { clientIps: ['192.168.1.11'] })];
  state.known = new Set();
  state.blockList = new Set();
  state.investigations = new Map();
  state.filter = 'all';
  state.hidePiholeBlocked = false;
  state.ipFilter = '192.168.1.10';
  state.search = '';

  assert.deepEqual(clientIpValues(withIp), ['192.168.1.10']);
  assert.deepEqual(clientIpValues(withFallback), ['192.168.1.10']);
  assert.deepEqual(Array.from(filteredDomains(), (item) => item.domain), ['with-ip.example', 'fallback.example']);
});

test('search and sorting apply after review filters', () => {
  const { state, filteredDomains } = loadApp();
  state.domains = [
    domain('zeta.example', { queryCount: 5, clients: ['Living Room'] }),
    domain('alpha.example', { queryCount: 2, clients: ['Office'] }),
    domain('beta.example', { queryCount: 10, clients: ['Office'] })
  ];
  state.known = new Set();
  state.blockList = new Set();
  state.investigations = new Map();
  state.filter = 'all';
  state.hidePiholeBlocked = false;
  state.ipFilter = 'all';
  state.search = 'office';
  state.sort = 'alpha';

  assert.deepEqual(Array.from(filteredDomains(), (item) => item.domain), ['alpha.example', 'beta.example']);
});

test('domain status and formatting helpers preserve UI expectations', () => {
  const { isPiholeBlocked, compactNumber, escapeHtml, truncateText } = loadApp();

  assert.equal(isPiholeBlocked(domain('gravity.example', { primaryStatus: 'GRAVITY' })), true);
  assert.equal(isPiholeBlocked(domain('blocked.example', { blockedCount: 1 })), true);
  assert.equal(isPiholeBlocked(domain('allowed.example')), false);
  assert.equal(compactNumber(999), '999');
  assert.equal(compactNumber(1250), '1.3k');
  assert.equal(escapeHtml('<script>alert("x")</script>'), '&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;');
  assert.equal(truncateText('one   two three', 10), 'one two t…');
});
