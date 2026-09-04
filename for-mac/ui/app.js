const $ = id => document.getElementById(id);
const API_BASE = localStorage.getItem('llamaGatewayBase') || 'http://127.0.0.1:8080';
const api = path => `${API_BASE}${path}`;

async function request(path, options = {}) {
  const response = await fetch(api(path), options);
  if (!response.ok) throw Error(`${response.status} ${response.statusText}`);
  return response;
}
async function json(path, options) { return (await request(path, options)).json(); }
async function text(path) { return (await request(path)).text(); }
function esc(value) { return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
function numberValue(id, fallback = 0) { const value = Number($(id).value); return Number.isFinite(value) ? value : fallback; }
function setValue(id, value, fallback = '') { if ($(id)) $(id).value = value ?? fallback; }
function setChecked(id, value) { if ($(id)) $(id).checked = Boolean(value); }

document.querySelectorAll('.tab').forEach(tab => tab.addEventListener('click', () => {
  document.querySelectorAll('.tab,.tab-page').forEach(x => x.classList.remove('active'));
  tab.classList.add('active');
  $(`page-${tab.dataset.tab}`).classList.add('active');
}));

function updateStatusRail(status, stats, resources, config) {
  $('rail-status').textContent = status.phase;
  $('rail-module').textContent = status.backend_ready ? `网关 已运行 · ${status.inflight} 个在途请求` : '网关 已停止';
  $('rail-resource').textContent = resources ? `CPU: ${resources.cpu_usage_percent.toFixed(1)}% | 内存: ${(resources.used_memory_bytes / 1073741824).toFixed(1)} GB` : 'CPU: — | 内存: —';
  $('rail-runtime').textContent = status.runtime_seconds == null ? '—' : `${status.runtime_seconds}s`;
  $('rail-token').textContent = stats ? `请求: ${stats.requests} · ${stats.prompt_tokens + stats.completion_tokens} tokens` : '请求: —';
  $('rail-slot').textContent = `槽位: ${status.bindings.length}`;
  const totalRestore = (stats?.restore_hits || 0) + (stats?.restore_misses || 0);
  $('rail-restore').textContent = totalRestore ? `${stats.restore_hits}/${totalRestore} (${Math.round(stats.restore_hits * 100 / totalRestore)}%)` : '暂无数据';
  $('rail-thinking').textContent = config?.thinking_mode || 'off';
}

async function refresh() {
  let status;
  try {
    status = await json('/__status__');
    $('phase-title').textContent = status.phase;
    $('footer-phase').textContent = status.phase;
    $('footer-backend').textContent = status.backend_ready ? '运行中' : '未启动';
    $('footer-slots').textContent = status.bindings.length;
    $('binding-count').textContent = `${status.bindings.length} 个绑定`;
    $('stat-inflight').textContent = status.inflight;
    $('health-dot').classList.toggle('ok', true);
    $('health-text').textContent = `网关在线 · 后端 ${status.backend_port}`;
    $('binding-table').innerHTML = status.bindings.map(binding => `<tr><td>${esc(binding.key)}</td><td>${esc(binding.app)}</td><td>${binding.slot}</td><td>${binding.kv_cache ? 'KV 已启用' : '无 KV'}${binding.preemptive ? ' · 强占' : ''}</td></tr>`).join('') || '<tr><td colspan="4" class="muted">暂无绑定</td></tr>';
  } catch (error) {
    $('health-dot').classList.remove('ok');
    $('health-text').textContent = '网关未连接';
    $('rail-module').textContent = '网关 已停止';
    return;
  }

  let config;
  try {
    config = await json('/__config__');
    loadConfig(config);
  } catch (_) { config = null; }
  let stats = null;
  try {
    stats = await json('/__stats__');
    $('stat-requests').textContent = stats.requests;
    $('stat-prompt').textContent = stats.prompt_tokens;
    $('stat-completion').textContent = stats.completion_tokens;
    $('stat-speed').textContent = `${stats.speed_tokens_per_second} tok/s`;
    $('stat-hit').textContent = `${stats.restore_hits}/${stats.restore_hits + stats.restore_misses}`;
    $('stat-slots').textContent = stats.slots_in_use;
  } catch (_) { /* keep the last values on transient errors */ }
  let resources = null;
  try {
    resources = await json('/__resources__');
    $('cpu').textContent = `${resources.cpu_usage_percent.toFixed(1)}%`;
    $('memory').textContent = `${(resources.used_memory_bytes / 1073741824).toFixed(1)} / ${(resources.total_memory_bytes / 1073741824).toFixed(1)} GB`;
    $('gpu').textContent = resources.gpu_backend;
  } catch (_) { /* resources are optional on macOS */ }
  updateStatusRail(status, stats, resources, config);

  try {
    const capabilities = await json('/__capabilities__');
    const lines = [];
    for (const [name, value] of Object.entries(capabilities)) {
      if (name === 'degradations') continue;
      if (typeof value === 'boolean') lines.push(`${name}: ${value ? '可用' : '不可用'}`);
      else lines.push(`${name}: ${value || '未知'}`);
    }
    $('capabilities-output').textContent = lines.join('\n');
    $('capability-note').textContent = capabilities.degradations?.length ? capabilities.degradations.join('\n') : '当前后端能力完整。';
  } catch (error) {
    $('capabilities-output').textContent = `能力探测失败：${error.message}`;
    $('capability-note').textContent = '后端未运行或不支持能力探测。';
  }
  try {
    const kind = encodeURIComponent($('log-kind').value);
    $('log-output').textContent = await text(`/__logs__?kind=${kind}&max_bytes=32768`);
  } catch (error) { $('log-output').textContent = `日志读取失败：${error.message}`; }
}

function parseArgs(raw) {
  try { const parsed = JSON.parse(raw); if (Array.isArray(parsed) && parsed.every(item => typeof item === 'string')) return parsed; } catch (_) { /* fallback below */ }
  return raw.trim() ? raw.trim().split(/\s+/) : [];
}
function modelFromArgs(args) {
  const index = args.indexOf('--model');
  if (index >= 0) return args[index + 1] || '';
  const inline = args.find(arg => arg.startsWith('--model='));
  return inline ? inline.slice('--model='.length) : '';
}
function argsWithModel(args, model) {
  const next = [...args];
  const index = next.indexOf('--model');
  const inline = next.findIndex(arg => arg.startsWith('--model='));
  if (model) {
    if (index >= 0) next[index + 1] = model;
    else if (inline >= 0) next[inline] = `--model=${model}`;
    else next.push('--model', model);
  } else if (index >= 0) next.splice(index, 2);
  else if (inline >= 0) next.splice(inline, 1);
  return next;
}
function loadConfig(config) {
  setValue('gateway-port', config.gateway_port); setValue('backend-port', config.backend_port);
  setValue('control-gateway-port', config.gateway_port); setValue('control-backend-port', config.backend_port);
  setValue('backend-executable', config.backend_executable || ''); setValue('backend-host', config.backend_host, '127.0.0.1');
  const args = config.backend_args || [];
  setValue('backend-args', JSON.stringify(args, null, 2)); setValue('backend-model', modelFromArgs(args));
  setValue('ready-timeout-ms', config.ready_timeout_ms); setValue('ready-poll-ms', config.ready_poll_ms); setValue('warming-delay-ms', config.warming_delay_ms);
  setValue('idle-timeout-ms', config.idle_timeout_ms); $('idle-mode').value = config.idle_timeout_ms === 0 ? 'off' : 'auto'; setValue('sleep-observe-ms', config.sleep_observe_ms); setValue('slot-count', config.slot_count);
  setChecked('token-guard-enabled', config.token_guard_enabled); setValue('context-size', config.context_size ?? ''); setValue('reserved-output-tokens', config.reserved_output_tokens); setValue('reserved-prompt-overhead', config.reserved_prompt_overhead); setChecked('context-overflow-recovery', config.context_overflow_recovery); setValue('thinking-mode', config.thinking_mode || 'off');
  setChecked('continuation-enabled', config.continuation_enabled); setValue('max-continuations', config.max_continuations); setValue('continuation-timeout-ms', config.continuation_timeout_ms); setChecked('crash-recovery-enabled', config.crash_recovery_enabled); setValue('max-crash-count', config.max_crash_count); setValue('auto-preemptive-prefixes', (config.auto_preemptive_prefixes || []).join(','));
  setValue('data-dir', config.data_dir); setValue('log-dir', config.log_dir || ''); setValue('log-max-bytes', config.log_max_bytes); setChecked('request-dump-enabled', config.request_dump_enabled);
}
async function control(path) { try { await request(path, { method: 'POST' }); await refresh(); } catch (error) { $('health-text').textContent = `操作失败：${error.message}`; } }
async function probe(path, target) { try { $(target).textContent = await (await request(`/__backend/${path}`)).text(); } catch (error) { $(target).textContent = `探针失败：${error.message}`; } }
async function saveConfig() {
  try {
    const config = await json('/__config__');
    const args = argsWithModel(parseArgs($('backend-args').value), $('backend-model').value.trim());
    const executable = $('backend-executable').value.trim();
    Object.assign(config, {
      gateway_port: numberValue('gateway-port', config.gateway_port), backend_host: $('backend-host').value.trim() || '127.0.0.1', backend_port: numberValue('backend-port', config.backend_port),
      backend_executable: executable || null, backend_args: args, ready_timeout_ms: numberValue('ready-timeout-ms', config.ready_timeout_ms), ready_poll_ms: numberValue('ready-poll-ms', config.ready_poll_ms), warming_delay_ms: numberValue('warming-delay-ms', config.warming_delay_ms), idle_timeout_ms: $('idle-mode').value === 'off' ? 0 : numberValue('idle-timeout-ms', config.idle_timeout_ms), sleep_observe_ms: numberValue('sleep-observe-ms', config.sleep_observe_ms), slot_count: numberValue('slot-count', config.slot_count),
      token_guard_enabled: $('token-guard-enabled').checked, context_size: $('context-size').value.trim() ? numberValue('context-size') : null, reserved_output_tokens: numberValue('reserved-output-tokens', config.reserved_output_tokens), reserved_prompt_overhead: numberValue('reserved-prompt-overhead', config.reserved_prompt_overhead), context_overflow_recovery: $('context-overflow-recovery').checked, thinking_mode: $('thinking-mode').value,
      continuation_enabled: $('continuation-enabled').checked, max_continuations: numberValue('max-continuations', config.max_continuations), continuation_timeout_ms: numberValue('continuation-timeout-ms', config.continuation_timeout_ms), crash_recovery_enabled: $('crash-recovery-enabled').checked, max_crash_count: numberValue('max-crash-count', config.max_crash_count), auto_preemptive_prefixes: $('auto-preemptive-prefixes').value.split(',').map(x => x.trim()).filter(Boolean),
      data_dir: $('data-dir').value.trim() || config.data_dir, log_dir: $('log-dir').value.trim() ? $('log-dir').value.trim() : null, log_max_bytes: numberValue('log-max-bytes', config.log_max_bytes), request_dump_enabled: $('request-dump-enabled').checked,
    });
    await json('/__config__', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(config) });
    $('config-status').textContent = '配置已保存（重启网关后完全生效）';
  } catch (error) { $('config-status').textContent = `保存失败：${error.message}`; }
}
async function refreshKv() { try { const snapshots = await json('/__kv__'); $('kv-output').textContent = snapshots.length ? snapshots.map(snapshot => `${snapshot.key} · slot ${snapshot.slot} · ${snapshot.size_bytes} B · ${snapshot.sha256.slice(0, 12)}`).join('\n') : '暂无快照'; } catch (error) { $('kv-output').textContent = `KV 列表失败：${error.message}`; } }
async function kvAction(action) { const payload = { slot: numberValue('kv-slot'), key: $('kv-key').value.trim() }; if (action !== 'clear' && !payload.key) { $('kv-output').textContent = '请填写快照 Key'; return; } try { await json(`/__kv/${action}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: action === 'clear' ? '{}' : JSON.stringify(payload) }); await refreshKv(); } catch (error) { $('kv-output').textContent = `KV 操作失败：${error.message}`; } }

$('refresh').onclick = refresh; $('wake').onclick = () => control('/__control/wake'); $('stop').onclick = () => control('/__control/stop'); $('probe-slots').onclick = () => probe('slots', 'slots-output'); $('probe-props').onclick = () => probe('props', 'props-output'); $('probe-metrics').onclick = () => probe('metrics', 'metrics-output'); $('save-config').onclick = saveConfig; $('kv-save').onclick = () => kvAction('save'); $('kv-restore').onclick = () => kvAction('restore'); $('kv-erase').onclick = () => kvAction('erase'); $('kv-clear').onclick = () => kvAction('clear'); $('log-kind').onchange = refresh;
$('control-gateway-port').onchange = event => setValue('gateway-port', event.target.value); $('control-backend-port').onchange = event => setValue('backend-port', event.target.value);
window.refresh = refresh; refreshKv(); refresh(); setInterval(refresh, 3000);
