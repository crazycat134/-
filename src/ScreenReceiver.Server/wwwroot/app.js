// ---------- 初始化 ----------
const token = new URLSearchParams(location.search).get('token');

const frameImg = document.getElementById('frame');
const frameMeta = document.getElementById('frameMeta');
const connStatus = document.getElementById('connStatus');
const analyzeBtn = document.getElementById('analyzeBtn');
const autoToggleBtn = document.getElementById('autoToggleBtn');
const modeManualBtn = document.getElementById('modeManual');
const modeAutoBtn = document.getElementById('modeAuto');
const autoPanel = document.getElementById('autoPanel');
const intervalSelect = document.getElementById('intervalSelect');
const analyzeStatus = document.getElementById('analyzeStatus');
const aiAnswer = document.getElementById('aiAnswer');

let framePolling = false;
let autoTimer = null;
let autoRunning = false;
let lastFrameId = -1;

if (!token) {
    connStatus.textContent = '未授权';
    connStatus.className = 'conn-status disconnected';
}

// ---------- 画面轮询（每秒一帧） ----------
async function pollFrame() {
    if (framePolling) return;
    framePolling = true;
    try {
        const res = await fetch('/api/v1/frames/latest', {
            headers: token ? { 'Authorization': 'Bearer ' + token } : {}
        });
        if (res.ok) {
            const blob = await res.blob();
            frameImg.src = URL.createObjectURL(blob);
            connStatus.textContent = '● 已连接';
            connStatus.className = 'conn-status connected';
            const fid = res.headers.get('X-Frame-Id');
            if (fid) {
                frameMeta.textContent = 'Frame #' + fid;
                lastFrameId = parseInt(fid);
            }
        } else if (res.status === 404) {
            connStatus.textContent = '等待画面…';
            connStatus.className = 'conn-status waiting';
        } else if (res.status === 401) {
            connStatus.textContent = '未授权';
            connStatus.className = 'conn-status disconnected';
        }
    } catch (e) {
        connStatus.textContent = '连接断开';
        connStatus.className = 'conn-status disconnected';
    } finally {
        framePolling = false;
        setTimeout(pollFrame, 1000);
    }
}
pollFrame();

// ---------- 模式切换 ----------
modeManualBtn.addEventListener('click', () => setMode('manual'));
modeAutoBtn.addEventListener('click', () => setMode('auto'));

function setMode(mode) {
    if (mode === 'manual') {
        modeManualBtn.classList.add('active');
        modeAutoBtn.classList.remove('active');
        autoPanel.classList.add('hidden');
        analyzeBtn.classList.remove('hidden');
        autoToggleBtn.classList.add('hidden');
        stopAuto();
    } else {
        modeAutoBtn.classList.add('active');
        modeManualBtn.classList.remove('active');
        autoPanel.classList.remove('hidden');
        analyzeBtn.classList.add('hidden');
        autoToggleBtn.classList.remove('hidden');
    }
}

// ---------- 手动识别 ----------
analyzeBtn.addEventListener('click', async () => {
    await runAnalysis('手动识别中…');
});

// ---------- 自动识别 ----------
autoToggleBtn.addEventListener('click', () => {
    if (autoRunning) stopAuto();
    else startAuto();
});

function startAuto() {
    const interval = parseInt(intervalSelect.value) * 1000;
    autoRunning = true;
    autoToggleBtn.textContent = '停止自动';
    autoToggleBtn.classList.add('stop');
    runAnalysis('自动识别中…');
    autoTimer = setInterval(() => runAnalysis('自动识别中…'), interval);
}

function stopAuto() {
    autoRunning = false;
    if (autoTimer) { clearInterval(autoTimer); autoTimer = null; }
    autoToggleBtn.textContent = '开始自动';
    autoToggleBtn.classList.remove('stop');
}

// ---------- 调 DeepSeek ----------
async function runAnalysis(statusText) {
    if (!token) {
        analyzeStatus.textContent = '未授权，无法识别';
        return;
    }
    analyzeStatus.textContent = statusText;
    analyzeBtn.disabled = true;
    try {
        const res = await fetch('/api/v1/analyze', {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token }
        });
        const data = await res.json();
        if (res.ok) {
            aiAnswer.innerHTML = renderMarkdown(data.answer || '(无结果)');
            analyzeStatus.textContent = '✅ 识别完成';
        } else {
            aiAnswer.innerHTML = '<div class="placeholder error">❌ ' +
                (data?.error?.message || '识别失败') + '</div>';
            analyzeStatus.textContent = '识别失败';
        }
    } catch (e) {
        analyzeStatus.textContent = '网络错误: ' + e.message;
    } finally {
        analyzeBtn.disabled = false;
    }
}

// ---------- 极简 Markdown 渲染（粗体/换行/列表） ----------
function renderMarkdown(text) {
    let html = text
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>')
        .replace(/^### (.+)$/gm, '<div class="md-h3">$1</div>')
        .replace(/^## (.+)$/gm, '<div class="md-h2">$1</div>')
        .replace(/^# (.+)$/gm, '<div class="md-h1">$1</div>')
        .replace(/^- (.+)$/gm, '<div class="md-li">• $1</div>')
        .replace(/\n/g, '<br>');
    return html;
}
