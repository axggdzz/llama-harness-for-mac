const $ = id => document.getElementById(id);
const API_BASE = localStorage.getItem('llamaGatewayBase') || 'http://127.0.0.1:8080';
const api = path => `${API_BASE}${path}`;
async function request(path, options = {}) { const response = await fetch(api(path), options); if (!response.ok) throw Error(`${response.status} ${response.statusText}`); return response; }
async function json(path, options) { return (await request(path, options)).json(); }
async function text(path) { return (await request(path)).text(); }
document.querySelectorAll('.tab').forEach(tab => tab.addEventListener('click', () => { document.querySelectorAll('.tab,.tab-page').forEach(x => x.classList.remove('active')); tab.classList.add('active'); $(`page-${tab.dataset.tab}`).classList.add('active'); }));
async function refresh() {
  try {
    const status = await json('/__status__'); $('phase-title').textContent = status.phase; $('footer-phase').textContent = status.phase; $('footer-backend').textContent = status.backend_ready ? '运行中' : '未启动'; $('footer-slots').textContent = status.bindings.length; $('binding-count').textContent = `${status.bindings.length} 个绑定`; $('health-dot').classList.toggle('ok', true); $('health-text').textContent = `网关在线 · 后端 ${status.backend_port}`;
    $('binding-table').innerHTML = status.bindings.map(binding => `<tr><td>${binding.key}</td><td>${binding.app}</td><td>${binding.slot}</td><td>${binding.kv_cache ? 'KV 已启用' : '无 KV'}</td></tr>`).join('');
    const config = await json('/__config__'); $('gateway-port').value = config.gateway_port; $('backend-port').value = config.backend_port; $('control-gateway-port').value = config.gateway_port; $('control-backend-port').value = config.backend_port; $('thinking-mode').value = config.thinking_mode;
    const stats = await json('/__stats__'); $('stat-requests').textContent = stats.requests; $('stat-prompt').textContent = stats.prompt_tokens; $('stat-completion').textContent = stats.completion_tokens; $('stat-speed').textContent = `${stats.speed_tokens_per_second} tok/s`; $('stat-hit').textContent = `${stats.restore_hits}/${stats.restore_hits + stats.restore_misses}`;
    const resources = await json('/__resources__'); $('cpu').textContent = `${resources.cpu_usage_percent.toFixed(1)}%`; $('memory').textContent = `${(resources.used_memory_bytes / 1073741824).toFixed(1)} / ${(resources.total_memory_bytes / 1073741824).toFixed(1)} GB`; $('gpu').textContent = resources.gpu_backend;
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
  } catch (error) { $('health-dot').classList.remove('ok'); $('health-text').textContent = '网关未连接'; }
}
async function control(path) { try { await request(path, { method: 'POST' }); await refresh(); } catch (error) { $('health-text').textContent = `操作失败：${error.message}`; } }
async function probe(path, target) { try { $(target).textContent = await (await request(`/__backend/${path}`)).text(); } catch (error) { $(target).textContent = `探针失败：${error.message}`; } }
async function saveConfig() { try { const config = await json('/__config__'); config.gateway_port = Number($('gateway-port').value); config.backend_port = Number($('backend-port').value); config.thinking_mode = $('thinking-mode').value; await json('/__config__', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(config) }); $('config-status').textContent = '配置已保存（重启网关后完全生效）'; } catch (error) { $('config-status').textContent = `保存失败：${error.message}`; } }
async function refreshKv() { try { const snapshots = await json('/__kv__'); $('kv-output').textContent = snapshots.length ? snapshots.map(snapshot => `${snapshot.key} · slot ${snapshot.slot} · ${snapshot.size_bytes} B · ${snapshot.sha256.slice(0, 12)}`).join('\n') : '暂无快照'; } catch (error) { $('kv-output').textContent = `KV 列表失败：${error.message}`; } }
async function kvAction(action) { const payload = { slot: Number($('kv-slot').value), key: $('kv-key').value.trim() }; if (action !== 'clear' && !payload.key) { $('kv-output').textContent = '请填写快照 Key'; return; } try { await json(`/__kv/${action}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: action === 'clear' ? '{}' : JSON.stringify(payload) }); await refreshKv(); } catch (error) { $('kv-output').textContent = `KV 操作失败：${error.message}`; } }
$('refresh').onclick = refresh; $('wake').onclick = () => control('/__control/wake'); $('stop').onclick = () => control('/__control/stop'); $('probe-slots').onclick = () => probe('slots', 'slots-output'); $('probe-props').onclick = () => probe('props', 'props-output'); $('probe-metrics').onclick = () => probe('metrics', 'metrics-output'); $('save-config').onclick = saveConfig; $('kv-save').onclick = () => kvAction('save'); $('kv-restore').onclick = () => kvAction('restore'); $('kv-erase').onclick = () => kvAction('erase'); $('kv-clear').onclick = () => kvAction('clear'); window.refresh = refresh; refreshKv(); refresh(); setInterval(refresh, 3000);
$('log-kind').onchange = refresh;
