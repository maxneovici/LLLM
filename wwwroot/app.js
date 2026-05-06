const state = {
  model: '',
  busy: false,
  currentDocument: null,
  mediaRecorder: null,
  micStream: null,
  audioContext: null,
  analyser: null,
  silenceMonitor: null,
  autoSendTimer: null,
  chunkTimer: null,
  stoppingForNextChunk: false,
  nextAudioChunkId: 0,
  nextTranscriptChunkId: 0,
  transcriptBuffer: new Map(),
  pendingTranscriptions: 0,
  recordingStartedAt: 0,
  recordingTimer: null
};

const modelSelect = document.querySelector('#modelSelect');
const refreshModels = document.querySelector('#refreshModels');
const runtimeRefresh = document.querySelector('#runtimeRefresh');
const runtimeStatus = document.querySelector('#runtimeStatus');
const startOllamaButton = document.querySelector('#startOllamaButton');
const warmModelButton = document.querySelector('#warmModelButton');
const unloadModelButton = document.querySelector('#unloadModelButton');
const pullModelButton = document.querySelector('#pullModelButton');
const loadedModels = document.querySelector('#loadedModels');
const systemPrompt = document.querySelector('#systemPrompt');
const reasoningMode = document.querySelector('#reasoningMode');
const dropzone = document.querySelector('#dropzone');
const fileInput = document.querySelector('#fileInput');
const documentCard = document.querySelector('#documentCard');
const memoryInput = document.querySelector('#memoryInput');
const memoryStatus = document.querySelector('#memoryStatus');
const memoryResults = document.querySelector('#memoryResults');
const refreshMemoryButton = document.querySelector('#refreshMemoryButton');
const rememberButton = document.querySelector('#rememberButton');
const searchMemoryButton = document.querySelector('#searchMemoryButton');
const ocrButton = document.querySelector('#ocrButton');
const invoiceButton = document.querySelector('#invoiceButton');
const messages = document.querySelector('#messages');
const chatForm = document.querySelector('#chatForm');
const voiceStatus = document.querySelector('#voiceStatus');
const messageInput = document.querySelector('#messageInput');
const voiceButton = document.querySelector('#voiceButton');
const healthDot = document.querySelector('#healthDot');
const healthText = document.querySelector('#healthText');
const healthSubtext = document.querySelector('#healthSubtext');

init();

async function init() {
  registerServiceWorker();
  await checkHealth();
  await loadModels();
  await refreshRuntimeStatus();
  await loadRecentMemory();
  bindEvents();
  addMessage('assistant', 'Drop a PDF/image or ask a question. Tool calling is enabled on the backend.');
}

function registerServiceWorker() {
  if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/sw.js').catch(error => console.warn('Service worker registration failed', error));
  }
}

function bindEvents() {
  refreshModels.addEventListener('click', loadModels);
  modelSelect.addEventListener('change', () => state.model = modelSelect.value);
  runtimeRefresh.addEventListener('click', refreshRuntimeStatus);
  startOllamaButton.addEventListener('click', startOllama);
  warmModelButton.addEventListener('click', warmSelectedModel);
  unloadModelButton.addEventListener('click', unloadSelectedModel);
  pullModelButton.addEventListener('click', pullSelectedModel);
  dropzone.addEventListener('click', () => fileInput.click());
  fileInput.addEventListener('change', () => uploadFile(fileInput.files[0]));
  refreshMemoryButton.addEventListener('click', loadRecentMemory);
  rememberButton.addEventListener('click', rememberMemory);
  searchMemoryButton.addEventListener('click', searchMemory);
  ocrButton.addEventListener('click', () => runDocumentAction('/api/ocr', 'OCR'));
  invoiceButton.addEventListener('click', () => runDocumentAction('/api/invoice', 'Invoice extraction'));
  voiceButton.addEventListener('click', toggleRecording);

  dropzone.addEventListener('dragover', event => {
    event.preventDefault();
    dropzone.classList.add('dragover');
  });

  dropzone.addEventListener('dragleave', () => dropzone.classList.remove('dragover'));
  dropzone.addEventListener('drop', event => {
    event.preventDefault();
    dropzone.classList.remove('dragover');
    uploadFile(event.dataTransfer.files[0]);
  });

  chatForm.addEventListener('submit', async event => {
    event.preventDefault();
    const message = messageInput.value.trim();
    if (!message || state.busy) return;
    messageInput.value = '';
    addMessage('user', message);
    await sendChat(message);
  });

  messageInput.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.shiftKey) {
      return;
    }

    event.preventDefault();
    chatForm.requestSubmit();
  });
}

async function toggleRecording() {
  if (state.mediaRecorder?.state === 'recording') {
    stopRecording({ autoSend: false });
    return;
  }

  if (state.busy) return;

  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    state.micStream = stream;
    state.nextAudioChunkId = 0;
    state.nextTranscriptChunkId = 0;
    state.transcriptBuffer.clear();
    startRecordingUi();
    startSilenceMonitor(stream);
    startVoiceChunk();
  } catch (error) {
    addMessage('error', `Microphone unavailable: ${error.message}`);
  }
}

function getRecorderOptions() {
  if (MediaRecorder.isTypeSupported('audio/webm;codecs=opus')) {
    return { mimeType: 'audio/webm;codecs=opus' };
  }

  return {};
}

function startVoiceChunk() {
  if (!state.micStream) {
    return;
  }

  const recorder = new MediaRecorder(state.micStream, getRecorderOptions());
  const chunks = [];
  const chunkId = state.nextAudioChunkId++;
  state.mediaRecorder = recorder;

  recorder.addEventListener('dataavailable', event => {
    if (event.data.size > 0) {
      chunks.push(event.data);
    }
  });

  recorder.addEventListener('stop', () => {
    if (chunks.length > 0) {
      const blob = new Blob(chunks, { type: chunks[0].type || recorder.mimeType || 'audio/webm' });
      void transcribeAudioChunk(blob, chunkId);
    }

    if (state.stoppingForNextChunk && state.micStream) {
      state.stoppingForNextChunk = false;
      startVoiceChunk();
    }
  });

  recorder.start();
  state.chunkTimer = window.setTimeout(() => rotateVoiceChunk(), 1600);
}

function rotateVoiceChunk() {
  if (state.mediaRecorder?.state !== 'recording') {
    return;
  }

  state.stoppingForNextChunk = true;
  state.mediaRecorder.stop();
}

function stopRecording({ autoSend }) {
  window.clearTimeout(state.chunkTimer);
  state.stoppingForNextChunk = false;

  if (state.mediaRecorder?.state === 'recording') {
    state.mediaRecorder.stop();
  }

  stopRecordingUi();
  cleanupRecordingResources();
  clearAutoSendTimer();

  if (autoSend) {
    scheduleAutoSend(700);
  }
}

function startRecordingUi() {
  state.recordingStartedAt = performance.now();
  voiceButton.classList.add('recording');
  voiceButton.textContent = 'Stop 0.0s';
  showVoiceStatus('Listening. Transcribing short chunks locally.', 'recording');
  state.recordingTimer = window.setInterval(() => {
    voiceButton.textContent = `Stop ${((performance.now() - state.recordingStartedAt) / 1000).toFixed(1)}s`;
  }, 250);
}

function stopRecordingUi() {
  if (state.recordingTimer) {
    window.clearInterval(state.recordingTimer);
  }

  voiceButton.classList.remove('recording');
  voiceButton.textContent = 'Mic';
}

function cleanupRecordingResources() {
  state.micStream?.getTracks().forEach(track => track.stop());
  state.audioContext?.close();
  window.clearInterval(state.silenceMonitor);
  state.micStream = null;
  state.audioContext = null;
  state.analyser = null;
  state.silenceMonitor = null;
}

function startSilenceMonitor(stream) {
  state.audioContext = new AudioContext();
  const source = state.audioContext.createMediaStreamSource(stream);
  state.analyser = state.audioContext.createAnalyser();
  state.analyser.fftSize = 2048;
  source.connect(state.analyser);

  const samples = new Uint8Array(state.analyser.fftSize);
  let silentSince = null;

  state.silenceMonitor = window.setInterval(() => {
    state.analyser.getByteTimeDomainData(samples);
    let sum = 0;

    for (const sample of samples) {
      const normalized = (sample - 128) / 128;
      sum += normalized * normalized;
    }

    const rms = Math.sqrt(sum / samples.length);
    const now = performance.now();

    if (rms < 0.018) {
      silentSince ??= now;
      const silentMs = now - silentSince;
      showVoiceStatus(`Listening. Auto-send after silence: ${Math.max(0, 4 - silentMs / 1000).toFixed(1)}s`, 'recording');

      if (silentMs >= 4000 && messageInput.value.trim()) {
        stopRecording({ autoSend: true });
      }
    } else {
      silentSince = null;
      clearAutoSendTimer();
      showVoiceStatus('Listening. Transcribing short chunks locally.', 'recording');
    }
  }, 250);
}

async function transcribeAudioChunk(chunk, chunkId) {
  if (chunk.size < 1024) {
    return;
  }

  state.pendingTranscriptions += 1;
  setVoiceBusy(true);
  showVoiceStatus(`Transcribing chunk ${state.pendingTranscriptions} locally`, 'transcribing');

  const progress = createVoiceProgress();

  try {
    const form = new FormData();
    form.append('audio', chunk, 'chunk.webm');

    progress.setStep('Sending audio chunk to local whisper.cpp');
    const response = await fetch('/api/transcribe', { method: 'POST', body: form });
    progress.setStep('Reading local partial transcript');
    const result = await readJsonOrThrow(response);
    const cleanedTranscript = cleanTranscript(result.text);
    bufferTranscriptChunk(chunkId, cleanedTranscript);
    messageInput.focus();
    progress.complete({ ...result, text: cleanedTranscript });
  } catch (error) {
    progress.fail(error);
    showVoiceStatus(`Voice chunk skipped: ${error.message}`, 'failed');
  } finally {
    state.pendingTranscriptions = Math.max(0, state.pendingTranscriptions - 1);
    setVoiceBusy(false);

    if (state.mediaRecorder?.state === 'recording') {
      showVoiceStatus('Listening. Transcribing short chunks locally.', 'recording');
    }
  }
}

function bufferTranscriptChunk(chunkId, text) {
  state.transcriptBuffer.set(chunkId, cleanTranscript(text));

  while (state.transcriptBuffer.has(state.nextTranscriptChunkId)) {
    appendTranscriptToComposer(state.transcriptBuffer.get(state.nextTranscriptChunkId));
    state.transcriptBuffer.delete(state.nextTranscriptChunkId);
    state.nextTranscriptChunkId += 1;
  }
}

function appendTranscriptToComposer(text) {
  const transcript = cleanTranscript(text);

  if (!transcript) {
    return;
  }

  const existingText = messageInput.value.trimEnd();
  const separator = existingText && !/[\s\n]$/.test(existingText) ? ' ' : '';
  messageInput.value = existingText ? `${existingText}${separator}${transcript}` : transcript;
  messageInput.dispatchEvent(new Event('input', { bubbles: true }));
}

function cleanTranscript(text) {
  return (text ?? '')
    .replace(/\[(?:BLANK_AUDIO|SILENCE|MUSIC|NO_AUDIO|INAUDIBLE)\]/gi, ' ')
    .replace(/\[[^\]]*blank[^\]]*\]/gi, ' ')
    .replace(/\((?:blank audio|silence|music|no audio|inaudible)\)/gi, ' ')
    .replace(/<\|[^>]+\|>/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function scheduleAutoSend(delayMs) {
  clearAutoSendTimer();
  state.autoSendTimer = window.setTimeout(() => {
    if (state.pendingTranscriptions > 0) {
      scheduleAutoSend(500);
      return;
    }

    if (messageInput.value.trim() && !state.busy) {
      chatForm.requestSubmit();
    }
  }, delayMs);
}

function clearAutoSendTimer() {
  if (state.autoSendTimer) {
    window.clearTimeout(state.autoSendTimer);
    state.autoSendTimer = null;
  }
}

function setVoiceBusy(busy) {
  voiceButton.disabled = busy && state.mediaRecorder?.state !== 'recording';
}

async function checkHealth() {
  try {
    const response = await fetch('/api/health');
    const result = await response.json();
    healthDot.className = `dot ${result.healthy ? 'ok' : 'bad'}`;
    healthText.textContent = result.healthy ? 'Ollama online' : 'Ollama unreachable';
  } catch (error) {
    healthDot.className = 'dot bad';
    healthText.textContent = 'Ollama unreachable';
    healthSubtext.textContent = error.message;
  }
}

async function loadModels() {
  setBusy(true);
  try {
    const response = await fetch('/api/models');
    if (!response.ok) throw new Error(await response.text());
    const models = await response.json();
    modelSelect.innerHTML = '';

    for (const model of models) {
      const option = document.createElement('option');
      option.value = model.name;
      option.textContent = `${model.name} (${formatGb(model.size)})`;
      modelSelect.appendChild(option);
    }

    state.model = modelSelect.value;
  } catch (error) {
    addMessage('error', `Could not load models: ${error.message}`);
  } finally {
    setBusy(false);
  }
}

async function refreshRuntimeStatus() {
  try {
    const response = await fetch('/api/ollama/status');
    const status = await readJsonOrThrow(response);
    runtimeStatus.textContent = status.healthy
      ? status.managedProcessRunning ? 'Ollama online, app-managed' : 'Ollama online'
      : 'Ollama offline';
    loadedModels.textContent = status.loadedModels?.length
      ? status.loadedModels.map(model => `${model.name} ${formatGb(model.size)}${model.processor ? ` on ${model.processor}` : ''}`).join('\n')
      : 'No loaded models.';
  } catch (error) {
    runtimeStatus.textContent = `Status failed: ${error.message}`;
    loadedModels.textContent = 'No loaded models.';
  }
}

async function startOllama() {
  await runRuntimeAction('Starting Ollama locally', async () => {
    const response = await fetch('/api/ollama/start', { method: 'POST' });
    await readJsonOrThrow(response);
    await checkHealth();
    await loadModels();
    await refreshRuntimeStatus();
  });
}

async function warmSelectedModel() {
  await runRuntimeAction(`Loading ${state.model}`, async () => {
    const response = await fetch('/api/ollama/warm', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model: state.model, keepAlive: '15m' })
    });
    const result = await readJsonOrThrow(response);
    addMessage('tool', `Loaded ${state.model}`, formatStats(result.stats));
    await refreshRuntimeStatus();
  });
}

async function unloadSelectedModel() {
  await runRuntimeAction(`Unloading ${state.model}`, async () => {
    const response = await fetch('/api/ollama/unload', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model: state.model })
    });
    await readJsonOrThrow(response);
    addMessage('tool', `Unloaded ${state.model}`);
    await refreshRuntimeStatus();
  });
}

async function pullSelectedModel() {
  const model = window.prompt('Model tag to pull locally', state.model || 'gemma4:e2b');

  if (!model) {
    return;
  }

  await runRuntimeAction(`Pulling ${model}`, async () => {
    const response = await fetch('/api/ollama/pull', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model })
    });
    await readJsonOrThrow(response);
    addMessage('tool', `Pulled ${model}`);
    await loadModels();
    await refreshRuntimeStatus();
  });
}

async function runRuntimeAction(statusText, action) {
  setBusy(true);
  runtimeStatus.textContent = statusText;

  try {
    await action();
  } catch (error) {
    addMessage('error', error.message);
    runtimeStatus.textContent = `Runtime action failed: ${error.message}`;
  } finally {
    setBusy(false);
  }
}

async function uploadFile(file) {
  if (!file || state.busy) return;
  setBusy(true);
  const form = new FormData();
  form.append('file', file);
  form.append('model', state.model);

  try {
    const response = await fetch('/api/documents', { method: 'POST', body: form });
    const result = await readJsonOrThrow(response);
    const document = result.document ?? result;
    state.currentDocument = document;
    renderDocument(document, result);
    addMessage('tool', `Uploaded and indexed ${document.originalName}\n${formatGb(document.sizeBytes)} ${document.kind}\n${result.indexedChunks ?? 0} memory chunk(s) via ${result.indexMethod ?? 'none'}`);
    await loadRecentMemory();
  } catch (error) {
    addMessage('error', `Upload failed: ${error.message}`);
  } finally {
    setBusy(false);
  }
}

async function rememberMemory() {
  const content = memoryInput.value.trim();
  if (!content || state.busy) return;

  setBusy(true);
  memoryStatus.textContent = 'Embedding memory locally';

  try {
    const response = await fetch('/api/memory', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content, title: content.slice(0, 80) })
    });
    await readJsonOrThrow(response);
    memoryInput.value = '';
    memoryStatus.textContent = 'Memory saved locally';
    await loadRecentMemory();
  } catch (error) {
    memoryStatus.textContent = `Memory failed: ${error.message}`;
    addMessage('error', error.message);
  } finally {
    setBusy(false);
  }
}

async function searchMemory() {
  const query = memoryInput.value.trim() || messageInput.value.trim();
  if (!query || state.busy) return;

  setBusy(true);
  memoryStatus.textContent = 'Vector searching locally';

  try {
    const response = await fetch(`/api/memory/search?q=${encodeURIComponent(query)}&limit=8`);
    const results = await readJsonOrThrow(response);
    renderMemoryResults(results, true);
    memoryStatus.textContent = `${results.length} memory match(es)`;
  } catch (error) {
    memoryStatus.textContent = `Search failed: ${error.message}`;
    addMessage('error', error.message);
  } finally {
    setBusy(false);
  }
}

async function loadRecentMemory() {
  try {
    const response = await fetch('/api/memory/recent?limit=8');
    const results = await readJsonOrThrow(response);
    renderMemoryResults(results, false);
    memoryStatus.textContent = `${results.length} recent local memories`;
  } catch (error) {
    memoryStatus.textContent = `Memory unavailable: ${error.message}`;
  }
}

async function forgetMemory(id) {
  if (!id || state.busy) return;

  setBusy(true);
  try {
    const response = await fetch(`/api/memory/${encodeURIComponent(id)}`, { method: 'DELETE' });
    await readJsonOrThrow(response);
    await loadRecentMemory();
  } catch (error) {
    addMessage('error', error.message);
  } finally {
    setBusy(false);
  }
}

async function sendChat(message) {
  setBusy(true);
  try {
    const response = await fetch('/api/chat/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        model: state.model,
        message,
        systemPrompt: systemPrompt.value,
        reasoningMode: reasoningMode.value
      })
    });

    if (!response.ok || !response.body) {
      throw new Error(await response.text());
    }

    await renderChatStream(response);
  } catch (error) {
    addMessage('error', error.message);
  } finally {
    setBusy(false);
  }
}

async function renderChatStream(response) {
  const assistant = createAssistantStreamMessage();
  const decoder = new TextDecoder();
  const reader = response.body.getReader();
  let buffer = '';

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split('\n');
    buffer = lines.pop() ?? '';

    for (const line of lines) {
      if (!line.trim()) continue;
      handleStreamEvent(JSON.parse(line), assistant);
    }
  }

  if (buffer.trim()) {
    handleStreamEvent(JSON.parse(buffer), assistant);
  }

  finalizeAssistantStreamMessage(assistant);
}

function handleStreamEvent(event, assistant) {
  switch (event.type) {
    case 'reasoning':
      assistant.reasoningText += event.text ?? '';
      renderMarkdownInto(assistant.reasoningBody, assistant.reasoningText);
      assistant.reasoning.hidden = false;
      break;
    case 'content':
      assistant.answerText += event.text ?? '';
      renderMarkdownInto(assistant.answer, assistant.answerText);
      break;
    case 'tool':
      if (event.toolResult) {
        addMessage('tool', `${event.toolResult.tool}\n${event.toolResult.content}`, `tool wall time ${formatMs(event.toolResult.durationMs)}`);
      }
      break;
    case 'stats':
      assistant.stats = event.stats;
      assistant.meta.textContent = formatStats(event.stats);
      break;
    case 'memory':
      assistant.memories = event.memories ?? [];
      renderUsedMemories(assistant);
      break;
    case 'route':
      assistant.route = event.route;
      renderRoute(assistant);
      break;
    case 'error':
      addMessage('error', event.error ?? 'Unknown stream error');
      break;
  }

  messages.scrollTop = messages.scrollHeight;
}

function createAssistantStreamMessage() {
  const bubble = document.createElement('article');
  bubble.className = 'message assistant streaming';

  const reasoning = document.createElement('details');
  reasoning.className = 'reasoning-box';
  reasoning.open = true;
  reasoning.hidden = true;

  const summary = document.createElement('summary');
  summary.textContent = 'Reasoning';
  const reasoningBody = document.createElement('div');
  reasoningBody.className = 'reasoning-body';
  reasoning.append(summary, reasoningBody);

  const answer = document.createElement('div');
  answer.className = 'answer-stream';
  const route = document.createElement('div');
  route.className = 'route-pill';
  route.hidden = true;
  const memory = document.createElement('details');
  memory.className = 'used-memory';
  memory.hidden = true;
  const memorySummary = document.createElement('summary');
  memorySummary.textContent = 'Memory Used';
  const memoryBody = document.createElement('div');
  memoryBody.className = 'used-memory-body';
  memory.append(memorySummary, memoryBody);
  const meta = document.createElement('div');
  meta.className = 'meta';

  bubble.append(route, reasoning, memory, answer, meta);
  messages.appendChild(bubble);
  messages.scrollTop = messages.scrollHeight;

  return { bubble, route, reasoning, reasoningBody, memory, memoryBody, answer, meta, reasoningText: '', answerText: '', routeInfo: null, memories: [], stats: null };
}

function renderRoute(assistant) {
  if (!assistant.route) return;

  assistant.routeInfo = assistant.route;
  assistant.route.hidden = false;
  const parts = [
    `${assistant.route.effort} mode`,
    assistant.route.think ? 'thinking on' : 'thinking off',
    assistant.route.useMemory ? 'memory on' : 'memory off',
    assistant.route.useTools ? 'tools on' : 'tools off'
  ];
  assistant.route.textContent = parts.join(' | ');
  assistant.route.title = assistant.route.reason ?? '';
}

function renderUsedMemories(assistant) {
  if (!assistant.memories?.length) {
    assistant.memory.hidden = true;
    return;
  }

  assistant.memory.hidden = false;
  assistant.memoryBody.innerHTML = '';

  for (const item of assistant.memories) {
    const row = documentCreate('div', 'used-memory-item');
    const title = documentCreate('strong', '', `${item.source}: ${item.title}`);
    const content = documentCreate('span', '', item.content);
    const score = item.score == null ? '' : `score ${item.score.toFixed(3)}`;
    const meta = documentCreate('em', '', score);
    row.append(title, content, meta);
    assistant.memoryBody.appendChild(row);
  }
}

function finalizeAssistantStreamMessage(assistant) {
  assistant.bubble.classList.remove('streaming');

  if (assistant.reasoningText) {
    assistant.reasoning.open = false;
  }

  if (!assistant.answerText.trim()) {
    assistant.answer.textContent = '(empty response)';
  }

  if (!assistant.meta.textContent && assistant.stats) {
    assistant.meta.textContent = formatStats(assistant.stats);
  }
}

async function runDocumentAction(endpoint, label) {
  if (!state.currentDocument) {
    addMessage('error', 'Upload a PDF/image first.');
    return;
  }

  setBusy(true);
  addMessage('user', `${label}: ${state.currentDocument.originalName}`);
  const progress = createDocumentProgress(label, state.currentDocument);

  try {
    progress.setStep('Uploading request to local app');
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model: state.model, pages: 3 })
    });
    progress.setStep('Reading local model response');
    const result = await readJsonOrThrow(response);
    progress.complete(result);
    renderDocumentResult(result);
  } catch (error) {
    progress.fail(error);
    addMessage('error', error.message);
  } finally {
    setBusy(false);
  }
}

function createDocumentProgress(label, document) {
  const startedAt = performance.now();
  const bubble = documentCreate('article', 'message tool progress-card');
  const header = documentCreate('div', 'progress-header');
  const spinner = documentCreate('div', 'scanner');
  const title = documentCreate('strong', '', `${label} running locally`);
  const subtitle = documentCreate('span', '', document.originalName);
  const timer = documentCreate('span', 'progress-timer', '0.0 s');
  const step = documentCreate('div', 'progress-step', 'Preparing local OCR pipeline');
  const track = documentCreate('div', 'progress-track');
  const fill = documentCreate('div', 'progress-fill');
  track.appendChild(fill);
  header.append(spinner, title, timer);
  bubble.append(header, subtitle, track, step);
  messages.appendChild(bubble);
  messages.scrollTop = messages.scrollHeight;

  const statusSteps = [
    'Rendering PDF/image locally',
    'Sending page image to Ollama',
    'Waiting for local vision model',
    'Extracting structured text',
    'Collecting timing and token stats'
  ];
  let statusIndex = 0;

  const interval = window.setInterval(() => {
    const elapsedMs = performance.now() - startedAt;
    timer.textContent = formatMs(elapsedMs);

    if (elapsedMs > 5000 && Math.floor(elapsedMs / 5000) > statusIndex && statusIndex < statusSteps.length) {
      step.textContent = statusSteps[statusIndex];
      statusIndex += 1;
    }
  }, 200);

  return {
    setStep(text) {
      step.textContent = text;
    },
    complete(result) {
      window.clearInterval(interval);
      bubble.classList.remove('progress-card');
      bubble.classList.add('progress-done');
      spinner.classList.add('done');
      timer.textContent = formatMs(result.totalDurationMs ?? performance.now() - startedAt);
      step.textContent = `${label} complete. ${result.pages?.length ?? 0} page(s) processed.`;
    },
    fail(error) {
      window.clearInterval(interval);
      bubble.classList.remove('progress-card');
      bubble.classList.add('progress-failed');
      spinner.classList.add('failed');
      step.textContent = `Failed: ${error.message}`;
    }
  };
}

function createVoiceProgress() {
  const startedAt = performance.now();
  showVoiceStatus('Preparing local speech model', 'transcribing', '0.0 s');

  const interval = window.setInterval(() => {
    updateVoiceStatusTime(formatMs(performance.now() - startedAt));
  }, 200);

  return {
    setStep(text) {
      showVoiceStatus(text, 'transcribing', formatMs(performance.now() - startedAt));
    },
    complete(result) {
      window.clearInterval(interval);
      showVoiceStatus(`Transcript inserted (${result.text.trim().length} chars). Press Enter to send.`, 'done', formatMs(result.durationMs ?? performance.now() - startedAt));
      window.setTimeout(hideVoiceStatus, 3500);
    },
    fail(error) {
      window.clearInterval(interval);
      showVoiceStatus(`Voice transcription failed: ${error.message}`, 'failed');
    }
  };
}

function showVoiceStatus(text, mode = 'idle', time = '') {
  voiceStatus.hidden = false;
  voiceStatus.className = `voice-status ${mode}`;
  voiceStatus.innerHTML = '';

  const pulse = documentCreate('span', 'voice-status-pulse');
  const label = documentCreate('span', 'voice-status-label', text);
  const timer = documentCreate('span', 'voice-status-time', time);
  voiceStatus.append(pulse, label, timer);
}

function updateVoiceStatusTime(time) {
  const timer = voiceStatus.querySelector('.voice-status-time');

  if (timer) {
    timer.textContent = time;
  }
}

function hideVoiceStatus() {
  if (state.mediaRecorder?.state === 'recording') {
    return;
  }

  voiceStatus.hidden = true;
  voiceStatus.innerHTML = '';
}

function documentCreate(tag, className = '', text = '') {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text) element.textContent = text;
  return element;
}

function renderChatResult(result) {
  for (const tool of result.toolResults ?? []) {
    addMessage('tool', `${tool.tool}\n${tool.content}`, `tool wall time ${formatMs(tool.durationMs)}`);
  }

  const meta = [formatStats(result.stats)];
  if (result.reasoning) meta.push('reasoning returned');
  addMessage('assistant', result.response || '(empty response)', meta.filter(Boolean).join(' | '));
}

function renderMarkdownInto(container, markdown) {
  container.innerHTML = '';
  container.append(...parseMarkdown(markdown));
}

function parseMarkdown(markdown) {
  const source = (markdown ?? '').replace(/\r\n/g, '\n');
  const nodes = [];
  const lines = source.split('\n');
  let paragraph = [];
  let list = null;

  const flushParagraph = () => {
    if (!paragraph.length) return;
    const element = document.createElement('p');
    appendInlineMarkdown(element, paragraph.join('\n'));
    nodes.push(element);
    paragraph = [];
  };

  const flushList = () => {
    if (!list) return;
    nodes.push(list);
    list = null;
  };

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    const fence = line.match(/^```(\w+)?\s*$/);

    if (fence) {
      flushParagraph();
      flushList();
      const codeLines = [];
      index += 1;

      while (index < lines.length && !lines[index].startsWith('```')) {
        codeLines.push(lines[index]);
        index += 1;
      }

      const pre = document.createElement('pre');
      const code = document.createElement('code');
      if (fence[1]) code.dataset.language = fence[1];
      code.textContent = codeLines.join('\n');
      pre.appendChild(code);
      nodes.push(pre);
      continue;
    }

    if (!line.trim()) {
      flushParagraph();
      flushList();
      continue;
    }

    const heading = line.match(/^(#{1,3})\s+(.+)$/);
    if (heading) {
      flushParagraph();
      flushList();
      const level = heading[1].length;
      const element = document.createElement(`h${level}`);
      appendInlineMarkdown(element, heading[2]);
      nodes.push(element);
      continue;
    }

    const listItem = line.match(/^\s*[-*]\s+(.+)$/);
    if (listItem) {
      flushParagraph();
      list ??= document.createElement('ul');
      const item = document.createElement('li');
      appendInlineMarkdown(item, listItem[1]);
      list.appendChild(item);
      continue;
    }

    const orderedItem = line.match(/^\s*\d+\.\s+(.+)$/);
    if (orderedItem) {
      flushParagraph();
      if (!list || list.tagName !== 'OL') {
        flushList();
        list = document.createElement('ol');
      }
      const item = document.createElement('li');
      appendInlineMarkdown(item, orderedItem[1]);
      list.appendChild(item);
      continue;
    }

    flushList();
    paragraph.push(line);
  }

  flushParagraph();
  flushList();
  return nodes.length ? nodes : [document.createTextNode('')];
}

function appendInlineMarkdown(parent, text) {
  const pattern = /(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|\[[^\]]+\]\([^\s)]+\))/g;
  let lastIndex = 0;

  for (const match of text.matchAll(pattern)) {
    parent.appendChild(document.createTextNode(text.slice(lastIndex, match.index)));
    const token = match[0];

    if (token.startsWith('**')) {
      const strong = document.createElement('strong');
      strong.textContent = token.slice(2, -2);
      parent.appendChild(strong);
    } else if (token.startsWith('*')) {
      const em = document.createElement('em');
      em.textContent = token.slice(1, -1);
      parent.appendChild(em);
    } else if (token.startsWith('`')) {
      const code = document.createElement('code');
      code.textContent = token.slice(1, -1);
      parent.appendChild(code);
    } else {
      const link = token.match(/^\[([^\]]+)\]\(([^\s)]+)\)$/);
      const anchor = document.createElement('a');
      anchor.textContent = link?.[1] ?? token;
      anchor.href = link?.[2] ?? '#';
      anchor.rel = 'noreferrer';
      parent.appendChild(anchor);
    }

    lastIndex = (match.index ?? 0) + token.length;
  }

  parent.appendChild(document.createTextNode(text.slice(lastIndex)));
}

function renderDocumentResult(result) {
  const text = result.pages.map(page => `Page ${page.page}\n${page.text}\n\n${formatStats(page.stats)}`).join('\n\n---\n\n');
  addMessage('tool', text, `${result.operation} total wall time ${formatMs(result.totalDurationMs)}`);
}

function renderMemoryResults(results, withScores) {
  memoryResults.innerHTML = '';

  if (!results?.length) {
    memoryResults.textContent = 'No memory found.';
    return;
  }

  for (const item of results) {
    const row = documentCreate('div', 'memory-result');
    const header = documentCreate('div', 'memory-result-header');
    const title = documentCreate('strong', '', item.title || 'Memory');
    const forget = documentCreate('button', 'secondary memory-forget', 'Forget');
    forget.type = 'button';
    forget.addEventListener('click', () => forgetMemory(item.id));
    const metaText = [item.source, withScores && item.score != null ? item.score.toFixed(3) : '', new Date(item.createdAt).toLocaleString()].filter(Boolean).join(' | ');
    const meta = documentCreate('span', 'memory-result-meta', metaText);
    const content = documentCreate('p', '', item.content);
    header.append(title, forget);
    row.append(header, meta, content);
    memoryResults.appendChild(row);
  }
}

function renderDocument(document, uploadResult = null) {
  documentCard.classList.remove('empty');
  const indexText = uploadResult ? `\nindexed chunks: ${uploadResult.indexedChunks ?? 0}` : '';
  documentCard.textContent = `${document.originalName}\n${document.kind} | ${formatGb(document.sizeBytes)}${indexText}`;
}

function addMessage(role, text, meta = '') {
  const bubble = document.createElement('article');
  bubble.className = `message ${role}`;
  const body = document.createElement('div');
  body.className = 'markdown-body';
  renderMarkdownInto(body, text);
  bubble.appendChild(body);

  if (meta) {
    const metaEl = document.createElement('div');
    metaEl.className = 'meta';
    metaEl.textContent = meta;
    bubble.appendChild(metaEl);
  }

  messages.appendChild(bubble);
  messages.scrollTop = messages.scrollHeight;
}

async function readJsonOrThrow(response) {
  const text = await response.text();
  const result = text ? JSON.parse(text) : null;
  if (!response.ok) {
    throw new Error(result?.error ?? text ?? response.statusText);
  }
  return result;
}

function setBusy(busy) {
  state.busy = busy;
  document.body.classList.toggle('busy', busy);
  for (const element of [refreshModels, runtimeRefresh, startOllamaButton, warmModelButton, unloadModelButton, pullModelButton, refreshMemoryButton, rememberButton, searchMemoryButton, ocrButton, invoiceButton, chatForm.querySelector('button[type="submit"]')]) {
    element.disabled = busy;
  }
}

function formatGb(bytes) {
  if (!Number.isFinite(bytes)) return '';
  return `${(bytes / 1024 / 1024 / 1024).toFixed(bytes > 1024 * 1024 * 1024 ? 2 : 4)} GB`;
}

function formatMs(ms) {
  if (!Number.isFinite(ms)) return 'unknown';
  if (ms >= 60000) return `${(ms / 60000).toFixed(2)} min`;
  if (ms >= 1000) return `${(ms / 1000).toFixed(2)} s`;
  return `${ms.toFixed(0)} ms`;
}

function formatStats(stats) {
  if (!stats) return '';
  const parts = [];
  if (stats.totalMs != null) parts.push(`total ${formatMs(stats.totalMs)}`);
  if (stats.loadMs != null) parts.push(`load ${formatMs(stats.loadMs)}`);
  if (stats.promptTokens != null) parts.push(`prompt ${stats.promptTokens} tok`);
  if (stats.responseTokens != null) parts.push(`response ${stats.responseTokens} tok`);
  if (stats.responseEvalMs != null && stats.responseTokens != null) {
    parts.push(`${(stats.responseTokens / (stats.responseEvalMs / 1000)).toFixed(1)} tok/s`);
  }
  return parts.join(' | ');
}
