const state = {
  domains: [],
  known: new Set(),
  blockList: new Set(),
  investigations: new Map(),
  investigationDomain: null,
  filter: 'needs-review',
  ipFilter: 'all',
  sort: 'queries',
  hidePiholeBlocked: true,
  search: '',
  connectionId: null,
  connectedUrl: null,
  piholeDenyList: new Set(),
  syncedDomains: new Set(),
  loading: false,
  syncing: false,
  sourceMeta: null
};

const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];

document.addEventListener('DOMContentLoaded', async () => {
  bindEvents();
  await loadReviewState();
  render();
  reconnectSavedPihole();
});

function bindEvents() {
  $('#connect-button').addEventListener('click', () => $('#connect-modal').showModal());
  $('#connect-modal').querySelector('.modal-close').addEventListener('click', () => $('#connect-modal').close());
  $('#cancel-connect').addEventListener('click', () => $('#connect-modal').close());
  $('#refresh-button').addEventListener('click', refreshData);
  $('#sync-block-list-button').addEventListener('click', syncBlockList);
  $('#mark-visible-button').addEventListener('click', markVisibleKnown);
  $('#investigation-modal').querySelector('.modal-close').addEventListener('click', () => $('#investigation-modal').close());
  $('#cancel-investigation').addEventListener('click', () => $('#investigation-modal').close());
  $('#investigation-form').addEventListener('submit', saveInvestigation);
  $('#search-input').addEventListener('input', (event) => { state.search = event.target.value.trim().toLowerCase(); renderTable(); });
  $('#range-select').addEventListener('change', async () => { await persistConfig(); await refreshData(); });
  $('#disk-checkbox').addEventListener('change', async () => { await persistConfig(); await refreshData(); });
  $('#hide-pihole-blocked').addEventListener('change', async () => { state.hidePiholeBlocked = $('#hide-pihole-blocked').checked; await persistConfig(); renderTable(); });
  $$('.filter-tab').forEach((button) => button.addEventListener('click', () => {
    state.filter = button.dataset.filter;
    $$('.filter-tab').forEach((tab) => tab.classList.toggle('active', tab === button));
    persistConfig();
    renderTable();
  }));
  $('#sort-select').addEventListener('change', (event) => { state.sort = event.target.value; persistConfig(); renderTable(); });
  $('#ip-filter').addEventListener('change', async (event) => { state.ipFilter = event.target.value; await persistConfig(); renderTable(); });
  $('#connect-form').addEventListener('submit', connectToPihole);
  $('#domain-table').addEventListener('click', handleTableAction);
}

async function loadReviewState() {
  try {
    const response = await fetch('/api/review-state', { cache: 'no-store' });
    if (!response.ok) throw new Error('state unavailable');
    const data = await response.json();
    state.known = new Set(data.knownDomains || []);
    state.blockList = new Set(data.blockedDomains || []);
    state.syncedDomains = new Set(data.piholeSyncedDomains || []);
    state.investigations = new Map((data.investigations || []).map((item) => [item.domain, item]));
  } catch (error) {
    showToast(`Could not load server review state: ${error.message}`);
  }

  try {
    const response = await fetch('/api/config', { cache: 'no-store' });
    if (!response.ok) throw new Error('configuration unavailable');
    applyConfig(await response.json());
  } catch (error) {
    showToast(`Could not load server configuration: ${error.message}`);
  }
}

function applyConfig(config) {
  state.connectedUrl = config.piholeUrl || null;
  if (state.connectedUrl) $('#pihole-url').value = state.connectedUrl;
  if (config.queryRange) $('#range-select').value = config.queryRange;
  if (typeof config.includeDisk === 'boolean') $('#disk-checkbox').checked = config.includeDisk;
  if (typeof config.hidePiholeBlocked === 'boolean') {
    state.hidePiholeBlocked = config.hidePiholeBlocked;
    $('#hide-pihole-blocked').checked = state.hidePiholeBlocked;
  }
  if (config.filter) state.filter = config.filter;
  if (config.ipFilter) state.ipFilter = config.ipFilter;
  if (config.sort) state.sort = config.sort;
  $$('.filter-tab').forEach((tab) => tab.classList.toggle('active', tab.dataset.filter === state.filter));
  $('#sort-select').value = state.sort;
}

async function reconnectSavedPihole() {
  if (!state.connectedUrl) return false;
  try {
    const response = await fetch('/api/pihole/connect', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url: state.connectedUrl, password: '', totp: null })
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Saved Pi-hole connection needs attention.');
    state.connectionId = data.connectionId;
    state.connectedUrl = data.url;
    await refreshPiholeDenyList();
    const refreshed = await refreshData();
    if (refreshed) showToast('Reconnected to Pi-hole. Fresh query data is ready.');
    return refreshed;
  } catch {
    render();
    return false;
  }
}

async function connectToPihole(event) {
  event.preventDefault();
  const error = $('#connect-error');
  const submit = $('#submit-connect');
  error.classList.add('hidden');
  submit.disabled = true;
  submit.textContent = 'Connecting…';
  try {
    const response = await fetch('/api/pihole/connect', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url: $('#pihole-url').value, password: $('#pihole-password').value, totp: $('#pihole-totp').value || null })
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Connection failed.');
    state.connectionId = data.connectionId;
    state.connectedUrl = data.url;
    await persistConfig();
    await refreshPiholeDenyList();
    $('#connect-modal').close();
    $('#pihole-password').value = '';
    showToast('Connected. Loading your query log…');
    await refreshData();
  } catch (connectionError) {
    error.textContent = connectionError.message;
    error.classList.remove('hidden');
  } finally {
    submit.disabled = false;
    submit.textContent = 'Connect and load';
  }
}

async function refreshData() {
  if (!state.connectionId) {
    state.domains = [];
    state.sourceMeta = null;
    render();
    showToast('Connect Pi-hole to load live domains.');
    return false;
  }

  state.domains = [];
  state.sourceMeta = null;
  state.loading = true;
  render();
  const seconds = $('#range-select').value;
  const params = new URLSearchParams();
  const now = Math.floor(Date.now() / 1000);
  if (seconds !== 'all') params.set('from', String(now - Number(seconds)));
  params.set('until', String(now));
  params.set('disk', String($('#disk-checkbox').checked));
  try {
    const response = await fetch(`/api/pihole/domains?${params}`, { headers: { 'X-Connection-Id': state.connectionId } });
    const data = await response.json();
    if (response.status === 401) {
      state.connectionId = null;
      throw new Error('Your Pi-hole session expired. Please connect again.');
    }
    if (!response.ok) throw new Error(data.error || 'Could not read the query log.');
    state.domains = data.domains || [];
    state.sourceMeta = data;
    updateRangeLabel();
    return true;
  } catch (error) {
    state.domains = [];
    state.sourceMeta = null;
    showToast(error.message);
    return false;
  } finally {
    state.loading = false;
    render();
  }
}

async function refreshPiholeDenyList() {
  state.piholeDenyList = new Set();
  if (!state.connectionId) return;
  try {
    const response = await fetch('/api/pihole/block-list', { headers: { 'X-Connection-Id': state.connectionId } });
    const data = await response.json();
    if (response.status === 401) {
      state.connectionId = null;
      throw new Error('Your Pi-hole session expired.');
    }
    if (!response.ok) throw new Error(data.error || 'Could not read Pi-hole’s block list.');
    state.piholeDenyList = new Set(data.domains || []);
  } catch {
    // The query-log review remains usable if Pi-hole's domain list cannot be read.
  }
}

async function syncBlockList() {
  const count = state.blockList.size;
  if (!count) {
    showToast('Your local block list is empty.');
    return;
  }
  if (!confirm(`Sync ${count} local ${count === 1 ? 'entry' : 'entries'} to Pi-hole’s exact deny list?\n\nThis will change Pi-hole filtering. Existing Pi-hole entries will be left alone, and nothing will be removed automatically.`)) return;

  state.syncing = true;
  render();
  try {
    if (!state.connectionId && state.connectedUrl) {
      const reconnected = await reconnectSavedPihole();
      if (!reconnected) throw new Error('Could not reconnect to Pi-hole. Use Reconnect to check the saved connection.');
    }
    if (!state.connectionId) throw new Error('Connect to Pi-hole before syncing the local block list.');
    const response = await fetch('/api/pihole/block-list/sync', { method: 'POST', headers: { 'X-Connection-Id': state.connectionId } });
    const data = await response.json();
    if (response.status === 401) {
      state.connectionId = null;
      throw new Error('Your Pi-hole session expired. Please connect again.');
    }
    if (!response.ok) throw new Error(data.error || 'Could not update Pi-hole’s block list.');
    state.syncedDomains = new Set(data.syncedDomains || state.syncedDomains);
    state.piholeDenyList = new Set([...state.piholeDenyList, ...(data.added || [])]);
    const added = (data.added || []).length;
    const alreadyPresent = (data.alreadyPresent || []).length;
    const errors = data.errors || [];
    if (errors.length) {
      showToast(`Pi-hole blocked ${added} domain${added === 1 ? '' : 's'} sent by the Domain Review app; ${alreadyPresent} already blocked, ${errors.length} failed.`);
    } else {
      showToast(`Pi-hole blocked ${added} domain${added === 1 ? '' : 's'} sent by the Domain Review app; ${alreadyPresent} already blocked.`);
    }
  } catch (error) {
    showToast(error.message);
  } finally {
    state.syncing = false;
    render();
  }
}

function openInvestigation(domain) {
  const existing = state.investigations.get(domain);
  state.investigationDomain = domain;
  $('#investigation-domain').textContent = domain;
  $('#investigation-notes').value = existing?.notes || '';
  $('#investigation-error').classList.add('hidden');
  $('#investigation-modal').showModal();
  $('#investigation-notes').focus();
}

async function saveInvestigation(event) {
  event.preventDefault();
  const domain = state.investigationDomain;
  const notes = $('#investigation-notes').value.trim();
  const error = $('#investigation-error');
  if (!domain || !notes) {
    error.textContent = 'Add a note before saving this investigation.';
    error.classList.remove('hidden');
    return;
  }

  const previousInvestigation = state.investigations.get(domain);
  const investigation = { domain, notes, updatedAt: new Date().toISOString() };
  state.investigations.set(domain, investigation);
  $('#investigation-modal').close();
  render();
  try {
    const response = await fetch('/api/review-state/investigations', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ domain, notes })
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Could not save the investigation note.');
    state.investigations = new Map((data.investigations || []).map((item) => [item.domain, item]));
    showToast(`${domain} marked for investigation`);
  } catch (saveError) {
    if (previousInvestigation) state.investigations.set(domain, previousInvestigation);
    else state.investigations.delete(domain);
    showToast(`${domain} could not be saved to the server: ${saveError.message}`);
  }
  render();
}

async function clearInvestigation(domain) {
  if (!state.investigations.has(domain)) return;
  if (!confirm(`Clear the investigation note for ${domain}?`)) return;
  const previousInvestigation = state.investigations.get(domain);
  state.investigations.delete(domain);
  render();
  try {
    const response = await fetch(`/api/review-state/investigations/${encodeURIComponent(domain)}`, { method: 'DELETE' });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Could not clear the investigation note.');
    state.investigations = new Map((data.investigations || []).map((item) => [item.domain, item]));
    showToast(`${domain} removed from investigations`);
  } catch (error) {
    state.investigations.set(domain, previousInvestigation);
    showToast(`${domain} could not be removed from the server: ${error.message}`);
  }
  render();
}

async function handleTableAction(event) {
  const button = event.target.closest('button[data-action]');
  if (!button) return;
  const domain = button.dataset.domain;
  if (button.dataset.action === 'known') await markKnown(domain);
  if (button.dataset.action === 'block') await blockDomain(domain, button);
  if (button.dataset.action === 'unblock') await unblockDomain(domain, button);
  if (button.dataset.action === 'unmark') await unmarkKnown(domain);
  if (button.dataset.action === 'investigate') openInvestigation(domain);
  if (button.dataset.action === 'clear-investigation') await clearInvestigation(domain);
}

async function markKnown(domain) {
  if (state.known.has(domain)) return;
  const previousKnown = new Set(state.known);
  state.known.add(domain);
  render();
  try {
    const response = await fetch('/api/review-state/known', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ domain }) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Could not save the known-domain decision.');
    state.known = new Set(data.knownDomains || []);
    showToast(`${domain} marked known`);
  } catch (error) {
    state.known = previousKnown;
    showToast(`${domain} could not be saved to the server: ${error.message}`);
  }
  render();
}

async function markVisibleKnown() {
  const visible = filteredDomains();
  if (!visible.length) return;
  const previousKnown = new Set(state.known);
  visible.forEach((item) => state.known.add(item.domain));
  try {
    const response = await fetch('/api/review-state/known/bulk', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ domains: visible.map((item) => item.domain) }) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Could not save the known-domain decisions.');
    state.known = new Set(data.knownDomains || []);
    showToast(`${visible.length} visible domains marked known`);
  } catch (error) {
    state.known = previousKnown;
    showToast(`Could not save the known-domain decisions to the server: ${error.message}`);
  }
  render();
}

async function unmarkKnown(domain) {
  const wasKnown = state.known.delete(domain);
  if (!wasKnown) return;
  render();
  try {
    const response = await fetch(`/api/review-state/known/${encodeURIComponent(domain)}`, { method: 'DELETE' });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Could not remove the known-domain decision.');
    state.known = new Set(data.knownDomains || []);
    showToast(`${domain} moved back to review`);
  } catch (error) {
    state.known.add(domain);
    showToast(`${domain} could not be updated on the server: ${error.message}`);
  }
  render();
}

async function blockDomain(domain, button) {
  if (state.blockList.has(domain)) return;
  if (!confirm(`Add ${domain} to your local block list?\n\nThis is only a review decision. It will not change Pi-hole or block network traffic.`)) return;
  button.disabled = true; button.textContent = 'Adding…';
  state.blockList.add(domain);
  render();
  try {
    const response = await fetch('/api/review-state/blocked', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ domain }) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Could not save the block-list decision.');
    state.blockList = new Set(data.blockedDomains || state.blockList);
    showToast(`${domain} added to the server-side block list`);
  } catch (error) {
    state.blockList.delete(domain);
    showToast(`${domain} could not be saved to the server: ${error.message}`);
  }
  render();
}

async function unblockDomain(domain, button) {
  if (!state.blockList.has(domain)) return;
  button.disabled = true; button.textContent = 'Removing…';
  state.blockList.delete(domain);
  render();
  try {
    const response = await fetch(`/api/review-state/blocked/${encodeURIComponent(domain)}`, { method: 'DELETE' });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Could not remove the block-list decision.');
    state.blockList = new Set(data.blockedDomains || []);
    showToast(`${domain} removed from the server-side block list`);
  } catch (error) {
    state.blockList.add(domain);
    showToast(`${domain} could not be removed from the server: ${error.message}`);
  }
  render();
}

function filteredDomains() {
  const sourceDomains = state.filter === 'blocked' ? domainsWithBlockListEntries() : state.domains;
  const filtered = sourceDomains.filter((item) => {
    const known = state.known.has(item.domain);
    const inBlockList = state.blockList.has(item.domain);
    const investigation = state.investigations.has(item.domain);
    if (state.hidePiholeBlocked && isPiholeBlocked(item) && state.filter !== 'blocked' && state.filter !== 'investigate' && !inBlockList && !investigation) return false;
    if (state.filter === 'needs-review' && (known || inBlockList || investigation)) return false;
    if (state.filter === 'known' && !known) return false;
    if (state.filter === 'investigate' && !investigation) return false;
    if (state.filter === 'blocked' && !inBlockList) return false;
    if (state.ipFilter !== 'all' && !clientIpValues(item).some((value) => value.toLowerCase() === state.ipFilter.toLowerCase())) return false;
    if (state.search) {
      const haystack = [item.domain, ...(item.clients || []), ...(item.clientIps || [])].join(' ').toLowerCase();
      if (!haystack.includes(state.search)) return false;
    }
    return true;
  });
  return filtered.sort((a, b) => {
    if (state.sort === 'alpha') return a.domain.localeCompare(b.domain);
    if (state.sort === 'recent') return (b.lastSeen || 0) - (a.lastSeen || 0);
    return b.queryCount - a.queryCount;
  });
}

function domainsWithBlockListEntries() {
  const domains = [...state.domains];
  const importedDomains = new Set(state.domains.map((item) => item.domain));
  for (const domain of state.blockList) {
    if (importedDomains.has(domain)) continue;
    domains.push({
      domain,
      queryCount: 0,
      blockedCount: 0,
      firstSeen: null,
      lastSeen: null,
      clients: [],
      primaryStatus: 'UNKNOWN',
      blockListOnly: true
    });
  }
  return domains;
}

function render() {
  updateIpFilterOptions();
  updateConnectionPill();
  updateStats();
  updateTabs();
  updateRangeLabel();
  $('#loading-state').classList.toggle('hidden', !state.loading);
  $('#mark-visible-button').disabled = state.loading || filteredDomains().length === 0;
  const syncButton = $('#sync-block-list-button');
  syncButton.disabled = state.syncing || (!state.connectionId && !state.connectedUrl) || state.blockList.size === 0;
  const syncLabel = state.connectionId ? 'Sync to Pi-hole' : (state.connectedUrl ? 'Reconnect & sync' : 'Connect to sync');
  syncButton.textContent = state.syncing ? 'Syncing…' : (state.blockList.size ? `${syncLabel} (${state.blockList.size})` : 'Sync to Pi-hole');
  renderTable();
}

function renderTable() {
  const table = $('#domain-table');
  const empty = $('#empty-state');
  const visible = filteredDomains();
  table.innerHTML = '';
  $('#truncated-notice').classList.toggle('hidden', !state.sourceMeta?.truncated);
  if (state.sourceMeta?.truncated) $('#truncated-notice').textContent = 'This range is larger than the safe import window, so the queue is showing the first 100,000 queries. Narrow the date range for a complete pass.';
  if (state.loading) { empty.classList.add('hidden'); return; }
  $('#empty-state h3').textContent = state.sourceMeta ? 'Nothing needs your attention' : 'Connect to Pi-hole to begin';
  $('#empty-state p').textContent = state.sourceMeta ? 'Try widening the range or switching to All domains.' : 'No cached query data is loaded.';
  empty.classList.toggle('hidden', visible.length > 0);
  visible.forEach((item) => table.appendChild(domainRow(item)));
  $('#result-summary').textContent = `${visible.length} ${visible.length === 1 ? 'domain' : 'domains'} shown`;
}

function domainRow(item) {
  const known = state.known.has(item.domain);
  const blocked = isPiholeBlocked(item);
  const inBlockList = state.blockList.has(item.domain);
  const investigation = state.investigations.get(item.domain);
  const tr = document.createElement('tr');
  const status = inBlockList ? 'block-list' : (investigation ? 'investigation' : (blocked ? 'blocked' : (item.primaryStatus === 'UNKNOWN' ? 'unknown' : 'allowed')));
  const statusLabel = inBlockList ? 'On block list' : (investigation ? 'Investigate' : (blocked ? 'Blocked in Pi-hole' : (item.primaryStatus === 'UNKNOWN' ? 'Unknown' : 'Allowed')));
  const clients = (item.clients || []).slice(0, 2).map((client) => `<span class="client-chip">${escapeHtml(client)}</span>`).join('');
  const extraClients = (item.clients || []).length > 2 ? `<span class="client-chip">+${item.clients.length - 2}</span>` : '';
  const bulletClass = inBlockList || blocked ? 'blocked' : (investigation ? 'investigation' : (known ? 'known' : ''));
  const notePreview = investigation ? ` · ${escapeHtml(truncateText(investigation.notes, 92))}` : '';
  const subLabel = inBlockList
    ? `${state.syncedDomains.has(item.domain)
      ? 'Sent to Pi-hole · exact deny list'
      : (state.piholeDenyList.has(item.domain) ? 'Already in Pi-hole · exact deny list' : 'Queued locally · not sent to Pi-hole')}${item.blockListOnly ? ' · not in current query import' : ''}`
    : (investigation ? `Investigation note${notePreview}` : (known ? 'You recognise this domain' : (blocked ? 'Already blocked by Pi-hole' : 'Not reviewed yet')));
  const blockAction = inBlockList
    ? '<button class="row-action block-action" data-action="unblock" data-domain="' + escapeAttribute(item.domain) + '">Remove from block list</button>'
    : (blocked ? '' : '<button class="row-action block-action" data-action="block" data-domain="' + escapeAttribute(item.domain) + '">Add to block list</button>');
  const investigationActions = investigation
    ? '<button class="row-action investigation-action" data-action="investigate" data-domain="' + escapeAttribute(item.domain) + '">Edit notes</button><button class="row-action" data-action="clear-investigation" data-domain="' + escapeAttribute(item.domain) + '">Clear investigation</button>'
    : '<button class="row-action investigation-action" data-action="investigate" data-domain="' + escapeAttribute(item.domain) + '">Investigate</button>';
  tr.innerHTML = `
    <td><div class="domain-cell"><span class="domain-bullet ${bulletClass}"></span><div><div class="domain-name">${escapeHtml(item.domain)}</div><div class="domain-sub">${subLabel}</div></div></div></td>
    <td><div class="activity">${activityBars(item.queryCount, item.domain)}<span class="activity-number">${compactNumber(item.queryCount)}</span></div></td>
    <td><div class="client-list">${clients}${extraClients || '<span class="client-chip">—</span>'}</div></td>
    <td><span class="time">${formatDate(item.lastSeen)}</span></td>
    <td><span class="status-badge ${status}"><span>${status === 'unknown' ? '○' : '●'}</span>${statusLabel}</span></td>
    <td><div class="row-actions">${known ? '<button class="row-action" data-action="unmark" data-domain="' + escapeAttribute(item.domain) + '">Review again</button>' : '<button class="row-action known-action" data-action="known" data-domain="' + escapeAttribute(item.domain) + '">✓ Known</button>'}${investigationActions}${blockAction}</div></td>`;
  return tr;
}

function updateStats() {
  const total = state.domains.length;
  const known = state.domains.filter((item) => state.known.has(item.domain)).length;
  const pending = pendingReviewDomains().length;
  $('#stat-total').textContent = compactNumber(total);
  $('#stat-review').textContent = compactNumber(pending);
  $('#stat-known').textContent = compactNumber(known);
  $('#stat-queries').textContent = compactNumber(state.domains.reduce((sum, item) => sum + item.queryCount, 0));
  $('#stat-source').textContent = state.sourceMeta ? `${state.sourceMeta.recordsRead.toLocaleString()} records · ${state.sourceMeta.pages} pages` : 'No live import yet';
  $('#stat-review-foot').textContent = pending ? `${pending} still unrecognised` : 'Nothing pending';
}

function updateTabs() {
  const known = state.domains.filter((item) => state.known.has(item.domain)).length;
  $('#tab-review-count').textContent = pendingReviewDomains().length;
  $('#tab-all-count').textContent = state.domains.length;
  $('#tab-known-count').textContent = known;
  $('#tab-investigate-count').textContent = state.investigations.size;
  $('#tab-blocked-count').textContent = state.blockList.size;
}

function updateConnectionPill() {
  const pill = $('#connection-pill');
  const dot = pill.querySelector('.status-dot');
  const host = state.connectedUrl ? new URL(state.connectedUrl).host : null;
  dot.classList.toggle('connected', Boolean(state.connectionId));
  $('#connection-label').textContent = state.connectionId ? host : (host ? `${host} · saved` : 'Not connected');
  $('#connect-button').textContent = host ? 'Reconnect' : 'Connect';
}

function updateRangeLabel() {
  const labels = { '86400': 'last 24 hours', '604800': 'last 7 days', '2592000': 'last 30 days', all: 'all retained history' };
  const range = labels[$('#range-select').value] || 'selected range';
  $('#range-label').textContent = state.sourceMeta ? `Pi-hole query log · ${range}` : `Connect Pi-hole · ${range}`;
}

async function persistConfig() {
  const config = {
    piholeUrl: state.connectedUrl || $('#pihole-url').value || null,
    queryRange: $('#range-select').value,
    includeDisk: $('#disk-checkbox').checked,
    hidePiholeBlocked: state.hidePiholeBlocked,
    filter: state.filter,
    ipFilter: state.ipFilter,
    sort: state.sort
  };
  try {
    const response = await fetch('/api/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(config) });
    if (!response.ok) {
      const data = await response.json();
      throw new Error(data.error || 'server rejected the configuration');
    }
  } catch (error) {
    showToast(`Could not save server configuration: ${error.message}`);
  }
}

function isPiholeBlocked(item) { return item.blockedCount > 0 || ['GRAVITY', 'REGEX', 'DENYLIST'].includes(item.primaryStatus); }
function clientIpValues(item) {
  const values = Array.isArray(item.clientIps) && item.clientIps.length ? item.clientIps : (item.clients || []);
  return values.filter(Boolean).map((value) => String(value));
}
function updateIpFilterOptions() {
  const select = $('#ip-filter');
  const values = [...new Set(state.domains.flatMap(clientIpValues))].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
  select.replaceChildren(new Option('All IPs', 'all'));
  values.forEach((value) => select.appendChild(new Option(value, value)));
  if (state.ipFilter !== 'all' && !values.some((value) => value.toLowerCase() === state.ipFilter.toLowerCase())) state.ipFilter = 'all';
  select.value = state.ipFilter;
}
function pendingReviewDomains() {
  return state.domains.filter((item) => !state.known.has(item.domain)
    && !state.blockList.has(item.domain)
    && !state.investigations.has(item.domain)
    && (!state.hidePiholeBlocked || !isPiholeBlocked(item)));
}

function activityBars(count, domain) {
  const seed = [...domain].reduce((sum, char) => sum + char.charCodeAt(0), 0);
  const heights = Array.from({ length: 12 }, (_, index) => 20 + ((seed * (index + 3)) % 70));
  const scale = Math.min(1, Math.max(.28, Math.log10(count + 1) / 4));
  return heights.map((height) => `<span class="bar" style="height:${Math.max(10, height * scale)}%"></span>`).join('');
}

function compactNumber(value) { return value >= 1000 ? `${(value / 1000).toFixed(value >= 10000 ? 0 : 1).replace('.0', '')}k` : String(value); }
function formatDate(value) { if (!value) return '—'; const date = new Date(value); const age = Date.now() - date.getTime(); if (age < 3600000) return `${Math.max(1, Math.round(age / 60000))} min ago`; if (age < 86400000) return `${Math.round(age / 3600000)} hr ago`; return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }); }
function escapeHtml(value) { return String(value).replace(/[&<>'"]/g, (char) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[char])); }
function escapeAttribute(value) { return escapeHtml(value); }
function truncateText(value, length) { const text = String(value || '').replace(/\s+/g, ' ').trim(); return text.length > length ? `${text.slice(0, length - 1)}…` : text; }
function showToast(message) { const toast = $('#toast'); toast.textContent = message; toast.classList.remove('hidden'); clearTimeout(showToast.timer); showToast.timer = setTimeout(() => toast.classList.add('hidden'), 3400); }
