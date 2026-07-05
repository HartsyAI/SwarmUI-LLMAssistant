/* ================================================================
   LLM Assistant - utils.js
   Shared state object, API helpers, rendering, and utilities.
   Loaded first before all other LLMA scripts.
   ================================================================ */

'use strict';

// -- Global State --
const LLMAState = {
    activeThreadId:    null,
    activeAssistantId: null,
    messages:          [],
    threads:           [],
    assistants:        [],
    settings:          {},
    threadParams:      {},
    currentModel:      null,
    availableModels:   [],
    // Side-by-side compare: when compareMode is on and compareModelB is set, a send fans the prompt
    // out to currentModel (lane A) and compareModelB (lane B). See chat/chat-compare.js.
    compareMode:       false,
    compareModelB:     null,
    // Per-lane device/backend override for compare mode, keyed by lane index (0/1) → backend instance id.
    // Set from the device dropdown in a column header; applied to the NEXT send. See chat/chat-compare.js.
    laneBackends:      {},
    isGenerating:      false,
    abortController:   null,
    attachedImage:     null,
    markdownEnabled:   true,
    enterToSend:       true,
    showTokens:        true,
    assets:            [],
    activeAssetId:     null,
    userProfile:       null,
    // Last-load error messages for each fetch the welcome gallery depends on. Null = ok.
    // Used by the welcome banner so users get actionable feedback instead of a blank gallery.
    loadErrors:        { assistants: null, models: null, tools: null },
    // True after llmaLoadModels() saw zero models with no API error. Used to render
    // a softer "no backend running" banner that's distinct from a hard load failure.
    noBackendsRunning: false,
};

// -- Shared Constants --
// Centralized so every module reads from one place. Module-local timing constants
// (companion cooldowns, etc.) stay where they're used; these are the values shared
// across files or that get tweaked often.
const LLMA_CONSTANTS = Object.freeze({
    // Toast auto-dismiss duration. Long enough to read, short enough not to linger.
    TOAST_DURATION_MS:  2600,
    // Sidebar / panel width bounds — clamp ranges for the resizable split bars.
    MIN_SIDEBAR_PX:     140,
    MAX_SIDEBAR_PX:     520,
    MIN_PANEL_PX:       180,
    MAX_PANEL_PX:       560,
    SPLIT_BAR_PX:       5,
    // Settings modal focus delay — gives the overlay layout a tick to settle before grabbing focus.
    MODAL_FOCUS_DELAY_MS: 60,
    // Sidebar focus delay after starting a chat — input element needs to exist first.
    INPUT_FOCUS_DELAY_MS: 80,
});

// -- API Helper --
function llmaRequest(route, args) {
    return new Promise((resolve, reject) => {
        genericRequest(route, args, resolve, (err) => {
            console.error(`[LLMAssistant] API error on ${route}:`, err);
            reject(err);
        });
    });
}

// -- CDN Loader --
const LLMA_LOADED_SCRIPTS = new Set();
const LLMA_LOADED_STYLES  = new Set();

function llmaLoadScript(src) {
    if (LLMA_LOADED_SCRIPTS.has(src)) return Promise.resolve();
    return new Promise((resolve, reject) => {
        const existing = document.querySelector(`script[src="${src}"]`);
        if (existing) { LLMA_LOADED_SCRIPTS.add(src); resolve(); return; }
        const s = document.createElement('script');
        s.src = src;
        s.onload  = () => { LLMA_LOADED_SCRIPTS.add(src); resolve(); };
        s.onerror = () => reject(new Error(`Failed to load script: ${src}`));
        document.head.appendChild(s);
    });
}

function llmaLoadStyle(href) {
    if (LLMA_LOADED_STYLES.has(href)) return;
    if (document.querySelector(`link[href="${href}"]`)) { LLMA_LOADED_STYLES.add(href); return; }
    const l = document.createElement('link');
    l.rel  = 'stylesheet';
    l.href = href;
    document.head.appendChild(l);
    LLMA_LOADED_STYLES.add(href);
}

// Base path for the vendored libraries shipped with the extension. NO CDN / external network —
// these resolve from the extension's own served assets (registered in LLMAssistantExtension.OnInit).
// See Assets/vendor/FETCH.md. Do NOT reintroduce https://cdn... URLs here.
const LLMA_VENDOR_BASE = '/ExtensionFile/LLMAssistantExtension/Assets/vendor';

async function llmaLoadVendoredLibs() {
    // Core libs (required for markdown rendering)
    llmaLoadStyle(`${LLMA_VENDOR_BASE}/highlight/github-dark.min.css`);
    await Promise.all([
        llmaLoadScript(`${LLMA_VENDOR_BASE}/marked/marked.min.js`),
        llmaLoadScript(`${LLMA_VENDOR_BASE}/highlight/highlight.min.js`),
        llmaLoadScript(`${LLMA_VENDOR_BASE}/dompurify/purify.min.js`),
    ]);
    if (window.marked && window.hljs) {
        window.marked.setOptions({
            highlight: (code, lang) => {
                const language = window.hljs.getLanguage(lang) ? lang : 'plaintext';
                return window.hljs.highlight(code, { language }).value;
            },
            breaks: true,
            gfm:    true,
        });
    }
    // Enhanced libs (KaTeX + Mermaid) — loaded async, non-blocking. KaTeX's CSS pulls its woff2 fonts
    // from the sibling fonts/ folder (relative to the CSS URL), which are vendored alongside it.
    llmaLoadStyle(`${LLMA_VENDOR_BASE}/katex/katex.min.css`);
    llmaLoadScript(`${LLMA_VENDOR_BASE}/katex/katex.min.js`)
        .then(() => llmaLoadScript(`${LLMA_VENDOR_BASE}/katex/auto-render.min.js`))
        .catch(() => console.warn('[LLMAssistant] KaTeX failed to load'));
    llmaLoadScript(`${LLMA_VENDOR_BASE}/mermaid/mermaid.min.js`)
        .then(() => {
            if (window.mermaid) {
                window.mermaid.initialize({
                    startOnLoad: false,
                    theme: 'dark',
                    securityLevel: 'strict',
                    fontFamily: 'inherit',
                });
            }
        })
        .catch(() => console.warn('[LLMAssistant] Mermaid failed to load'));
}

// -- Markdown Renderer --
let llmaMermaidCounter = 0;

function llmaRenderMarkdown(text) {
    if (!LLMAState.markdownEnabled || !window.marked || !window.DOMPurify) {
        return `<p>${llmaEscapeHtml(text).replace(/\n/g, '<br>')}</p>`;
    }
    let processed = text;
    // Reasoning models (Qwen3, DeepSeek-R1, GLM thinking, …) wrap their chain-of-thought in <think>…</think>.
    // Pull those out into a collapsible block so the visible answer isn't buried in raw reasoning. Handles an
    // unclosed <think> mid-stream (everything after it is still "thinking" until the closing tag arrives).
    const thinkBlocks = [];
    processed = processed.replace(/<think>([\s\S]*?)<\/think>/gi, (_, inner) => {
        thinkBlocks.push(inner.trim());
        return `%%LLMA_THINK_${thinkBlocks.length - 1}%%`;
    });
    const openThink = processed.match(/<think>([\s\S]*)$/i);
    if (openThink) {
        thinkBlocks.push(openThink[1].trim());
        processed = processed.slice(0, openThink.index) + `%%LLMA_THINK_${thinkBlocks.length - 1}%%`;
    }

    // Protect LaTeX delimiters from markdown parsing
    const latexBlocks = [];
    // Block math: $$...$$
    processed = processed.replace(/\$\$([\s\S]+?)\$\$/g, (_, tex) => {
        latexBlocks.push({ tex, display: true });
        return `%%LLMA_LATEX_${latexBlocks.length - 1}%%`;
    });
    // Inline math: $...$  (not preceded/followed by space+digit pattern to avoid false positives)
    processed = processed.replace(/(?<!\$)\$(?!\$)(.+?)(?<!\$)\$(?!\$)/g, (_, tex) => {
        latexBlocks.push({ tex, display: false });
        return `%%LLMA_LATEX_${latexBlocks.length - 1}%%`;
    });

    // Extract mermaid code blocks before markdown parsing
    const mermaidBlocks = [];
    processed = processed.replace(/```mermaid\s*\n([\s\S]*?)```/g, (_, code) => {
        mermaidBlocks.push(code.trim());
        return `%%LLMA_MERMAID_${mermaidBlocks.length - 1}%%`;
    });

    const rawHtml = window.marked.parse(processed);
    // Hardened allowlist. Deliberately excludes:
    //   - `style`  → blocks CSS-based UI redressing / clickjacking / data-exfil via url() in model output
    //   - `id`     → blocks DOM clobbering (attacker-chosen ids colliding with getElementById/globals)
    //   - `foreignObject` → classic SVG sanitizer-bypass / mutation-XSS vector
    // KaTeX math, Mermaid blocks, and the code "Copy" button are injected AFTER this sanitize pass,
    // so dropping these does not affect their (app-generated) markup.
    const sanitizeConfig = {
        ALLOWED_TAGS: [
            'p','br','strong','em','del','code','pre','ul','ol','li',
            'h1','h2','h3','h4','h5','h6','blockquote','hr',
            'table','thead','tbody','tr','td','th','a','img',
            'span','div','svg','path','g','rect','line','circle','text',
            'polygon','polyline','ellipse','marker','defs','clipPath','use',
            'tspan',
        ],
        ALLOWED_ATTR: [
            'href','class','target','rel','src','alt','title',
            'd','fill','stroke','stroke-width','transform',
            'viewBox','xmlns','width','height','x','y','cx','cy','r',
            'rx','ry','x1','y1','x2','y2','points','marker-end',
            'text-anchor','dominant-baseline','font-size',
            'clip-path','aria-label','role','tabindex',
        ],
    };
    const clean   = window.DOMPurify.sanitize(rawHtml, sanitizeConfig);

    // Restore LaTeX placeholders
    let result = clean;
    for (let i = 0; i < latexBlocks.length; i++) {
        const { tex, display } = latexBlocks[i];
        let rendered;
        if (window.katex) {
            try {
                rendered = window.katex.renderToString(tex, {
                    displayMode: display,
                    throwOnError: false,
                    output: 'html',
                });
            } catch {
                rendered = `<code>${llmaEscapeHtml(tex)}</code>`;
            }
        } else {
            rendered = display ? `<pre><code>${llmaEscapeHtml(tex)}</code></pre>` : `<code>${llmaEscapeHtml(tex)}</code>`;
        }
        result = result.replace(`%%LLMA_LATEX_${i}%%`, rendered);
    }

    // Restore Mermaid placeholders
    for (let i = 0; i < mermaidBlocks.length; i++) {
        const mId = `llma-mermaid-${++llmaMermaidCounter}`;
        const placeholder = `<div class="llma-mermaid-block" id="${mId}" data-mermaid="${llmaEscapeHtml(mermaidBlocks[i])}"><pre><code>${llmaEscapeHtml(mermaidBlocks[i])}</code></pre></div>`;
        result = result.replace(
            new RegExp(`(<p>)?%%LLMA_MERMAID_${i}%%(</p>)?`),
            placeholder
        );
    }

    // Restore <think> reasoning blocks as collapsible sections (app-generated shell; inner model text is
    // markdown-rendered then sanitized with the same allowlist). Injected after the main sanitize pass, so
    // the <details>/<summary> shell survives without widening the allowlist for model output.
    for (let i = 0; i < thinkBlocks.length; i++) {
        const innerHtml = window.DOMPurify.sanitize(window.marked.parse(thinkBlocks[i] || ''), sanitizeConfig);
        const block = `<details class="llma-think"><summary class="llma-think-summary">💭 Reasoning</summary><div class="llma-think-body">${innerHtml}</div></details>`;
        result = result.replace(new RegExp(`(<p>)?%%LLMA_THINK_${i}%%(</p>)?`), block);
    }

    // Add copy buttons to code blocks
    result = result.replace(/<pre>/g, '<pre><button class="llma-copy-code-btn" type="button">Copy</button>');

    return result;
}

/** Call after inserting rendered HTML into the DOM to activate mermaid diagrams. */
function llmaPostRenderMermaid(containerEl) {
    if (!window.mermaid || !containerEl) return;
    const blocks = containerEl.querySelectorAll('.llma-mermaid-block[data-mermaid]');
    blocks.forEach(async (block) => {
        if (block.dataset.rendered) return;
        block.dataset.rendered = 'true';
        try {
            const { svg } = await window.mermaid.render(block.id + '-svg', block.dataset.mermaid);
            block.innerHTML = svg;
        } catch {
            // Leave the code block fallback visible
        }
    });
}

function llmaCopyCode(btn) {
    const code = btn.parentElement.querySelector('code');
    if (!code) return;
    navigator.clipboard.writeText(code.textContent || '').then(() => {
        const prev = btn.textContent;
        btn.textContent = 'Copied!';
        setTimeout(() => { btn.textContent = prev; }, 1500);
    });
}

// Delegated handler for the code-block "Copy" buttons. Registered once (CSP-clean — no inline
// onclick), works for every dynamically-inserted markdown block (chat bubbles, asset viewer, …).
document.addEventListener('click', (e) => {
    const btn = e.target.closest?.('.llma-copy-code-btn');
    if (btn) llmaCopyCode(btn);
});

// -- HTML Escape --
function llmaEscapeHtml(str) {
    if (!str) return '';
    return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
              .replace(/"/g, '&quot;').replace(/'/g, '&#039;');
}

// -- ID Generation --
function llmaGenerateId() {
    return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 9)}`;
}

// -- Vision capability inference --
// Best-effort, fail-open: returns true ONLY when the model is confidently vision-capable.
// Unknown models return null so the UI can keep the paperclip enabled (no regression for
// the long tail of self-hosted models we don't recognize). When the backend later exposes
// a real vision flag in LLMModelInfo.Metadata, that takes precedence.
function llmaIsVisionModel(model) {
    if (!model) return null;
    if (model.metadata && (model.metadata.vision === 'true' || model.metadata.vision === true)) return true;
    if (model.metadata && (model.metadata.vision === 'false' || model.metadata.vision === false)) return false;
    const id = (model.id || '').toLowerCase();
    const name = (model.name || '').toLowerCase();
    const family = (model.family || '').toLowerCase();
    const provider = (model.provider || '').toLowerCase();
    const hay = `${id} ${name} ${family} ${provider}`;
    // Known vision-capable families. Conservative — only well-attested ones.
    if (/claude-(opus-4|sonnet-4|haiku-4|3-5-sonnet|3-5-haiku|3-opus)/.test(hay)) return true;
    if (/(gpt-4o|gpt-4\.1|gpt-4-turbo|gpt-4-vision|chatgpt-4)/.test(hay)) return true;
    if (/(gemini-1\.5|gemini-2|gemini-pro-vision|gemini-flash)/.test(hay)) return true;
    if (/(llava|moondream|minicpm-v|qwen2-vl|qwen-vl|internvl|bakllava|llama-?3\.2-vision|phi-3-vision|phi-3\.5-vision|pixtral|molmo)/.test(hay)) return true;
    return null;
}

// -- Error formatting --
// Pulls a one-line human-readable string out of whatever genericRequest's error callback hands us.
// genericRequest can hand back: an Error, a string, a {message} object, or null on network failure.
function llmaShortError(err) {
    if (!err) return null;
    if (typeof err === 'string') return err;
    if (err.message) return err.message;
    if (err.error)   return err.error;
    try { return String(err); } catch { return null; }
}

// Maps a raw error (server string, WS transport event, or thrown exception) to a friendly
// { title, hint, detail } triple. The goal: never show a bare opaque "WebSocket error" again —
// recognized failures get a plain-language title plus an actionable hint, while unrecognized ones
// surface the raw text as the title (so we never hide real detail). `detail` is the raw string,
// kept for a hover tooltip / console.
function llmaHumanizeError(raw) {
    const text = (llmaShortError(raw) || '').toString().trim();
    const low  = text.toLowerCase();
    const make = (title, hint) => ({ title, hint, detail: text });

    // Empty / generic transport failures — the most common opaque case.
    if (!text || low === 'websocket error' || low.includes('generic progressevent') || low.includes('did the server crash') || low.includes('failed to send request')) {
        return make('Couldn’t reach the LLM backend',
            'The connection dropped before a reply came back. Make sure SwarmUI and at least one LLM backend are running (Server > Backends), then try again.');
    }
    if (low.includes('no model') || low.includes('model not found') || low.includes('no such model') || low.includes('unknown model') || low.includes('model is required') || low.includes('no provider')) {
        return make('No usable model',
            'Pick a model from the dropdown at the top of the chat. If the list is empty, start an LLM backend under Server > Backends and click the refresh icon.');
    }
    if (low.includes('no llm backend') || low.includes('no backend') || low.includes('backend is running') || low.includes('not running') || low.includes('no running')) {
        return make('No LLM backend is running',
            'Add or start one under Server > Backends, then click the refresh icon next to the model dropdown.');
    }
    if (low.includes('api key') || low.includes('apikey') || low.includes('unauthorized') || low.includes('401') || low.includes('invalid key') || low.includes('authentication')) {
        return make('The backend rejected your credentials',
            'Check your API key for this provider in the User tab, then try again.');
    }
    if (low.includes('permission') || low.includes('not allowed') || low.includes('forbidden') || low.includes('403')) {
        return make('You don’t have permission for this',
            'Ask an admin to grant the relevant llm_* permission for your account.');
    }
    if (low.includes('out of memory') || low.includes('oom') || low.includes('cuda') || low.includes('vram') || low.includes('cublas') || low.includes('allocat')) {
        return make('The model ran out of memory',
            'Try a smaller model, lower Max Tokens / Context in the parameters popover, or free up VRAM, then retry.');
    }
    if (low.includes('timeout') || low.includes('timed out')) {
        return make('The request timed out',
            'The backend took too long — it may still be loading a large model. Wait a moment and try again.');
    }
    if (low.includes('rate limit') || low.includes('429') || low.includes('quota')) {
        return make('Rate limit or quota reached',
            'The provider is throttling requests. Wait a bit, or check your plan’s usage limits, then retry.');
    }
    if (low.includes('connect') || low.includes('refused') || low.includes('econnrefused') || low.includes('unreachable')) {
        return make('Couldn’t connect to the backend',
            'The model server isn’t reachable. Check the backend’s address/status under Server > Backends.');
    }
    // Unknown — surface the raw text as the title, no invented hint.
    return make(text || 'Something went wrong', null);
}

// -- User-action wrapper --
// Standardizes error handling for user-initiated UI actions (save, delete, rename, send, etc.):
// catches, surfaces a toast with the server's error message when available, logs full details
// to the console for debugging, and returns null so callers don't have to think about exceptions.
// Background polling / hover / race-recovery code should NOT use this — silent fail is correct
// for those paths since the user didn't ask for the action.
async function llmaUserAction(fn, errorPrefix = 'Action failed') {
    try {
        return await fn();
    } catch (e) {
        const detail = llmaShortError(e);
        console.error(`[LLMAssistant] ${errorPrefix}:`, e);
        llmaShowToast(detail ? `${errorPrefix}: ${detail}` : errorPrefix, 'error');
        return null;
    }
}

// -- Toast Notifications --
let llmaToastTimer = null;

function llmaShowToast(message, type = 'info') {
    const container = document.getElementById('llma-container');
    if (!container) return;
    const existing = container.querySelector('.llma-toast');
    if (existing) existing.remove();
    if (llmaToastTimer) clearTimeout(llmaToastTimer);
    const toast = document.createElement('div');
    toast.className = `llma-toast ${type}`;
    toast.textContent = message;
    container.appendChild(toast);
    llmaToastTimer = setTimeout(() => toast.remove(), LLMA_CONSTANTS.TOAST_DURATION_MS);
}

// -- Date / Time --
function llmaRelativeTime(isoString) {
    const now  = Date.now();
    const date = new Date(isoString).getTime();
    const diff = Math.floor((now - date) / 1000);
    if (diff < 60)    return 'just now';
    if (diff < 3600)  return `${Math.floor(diff / 60)}m ago`;
    if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
    return new Date(isoString).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

function llmaGroupByDate(threads) {
    const now    = Date.now();
    const oneDay = 86400000;
    const groups = { Today: [], Yesterday: [], 'This week': [], Older: [] };
    for (const t of threads) {
        const age = now - new Date(t.updated || t.created).getTime();
        if (age < oneDay)           groups['Today'].push(t);
        else if (age < 2 * oneDay)  groups['Yesterday'].push(t);
        else if (age < 7 * oneDay)  groups['This week'].push(t);
        else                        groups['Older'].push(t);
    }
    return groups;
}

// -- Color Utilities --
function llmaShiftColor(hex, amount) {
    const num = parseInt(hex.replace('#', ''), 16);
    const r   = Math.max(0, Math.min(255, (num >> 16) + amount));
    const g   = Math.max(0, Math.min(255, ((num >> 8) & 0x00FF) + amount));
    const b   = Math.max(0, Math.min(255, (num & 0x0000FF) + amount));
    return `#${((r << 16) | (g << 8) | b).toString(16).padStart(6, '0')}`;
}

/** Returns a CSS gradient background string. Uses CSS vars when no custom color is set. */
function llmaGradientBg(color) {
    if (color) return `linear-gradient(135deg,${color},${llmaShiftColor(color, -40)})`;
    return 'linear-gradient(135deg, var(--emphasis), color-mix(in srgb, var(--emphasis) 70%, black))';
}

// -- Category Icons --
const LLMA_CATEGORY_ICONS = {
    chat:     '\u{1F4AC}',
    vision:   '\u{1F441}',
    prompt:   '\u2726',
    code:     '\u2328',
    creative: '\u{1F3A8}',
    analyze:  '\u{1F50D}',
    custom:   '\u2B50',
};

function llmaCategoryIcon(category) {
    return LLMA_CATEGORY_ICONS[category] || '\u2726';
}

// -- Popover Click Away --
function llmaPopoverClickAway(popoverEl, triggerEl, onClose) {
    const handler = (e) => {
        if (!popoverEl.contains(e.target) && !triggerEl.contains(e.target)) {
            onClose();
            document.removeEventListener('click', handler, true);
        }
    };
    setTimeout(() => document.addEventListener('click', handler, true), 10);
    return () => document.removeEventListener('click', handler, true);
}

// -- Approximate Token Count --
function llmaApproxTokens(messages) {
    const totalChars = messages.reduce((sum, m) => sum + (m.content?.length || 0), 0);
    return Math.round(totalChars / 4);
}

// -- Image to Base64 --
function llmaFileToBase64(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload  = () => resolve(reader.result);
        reader.onerror = reject;
        reader.readAsDataURL(file);
    });
}

function llmaDataUrlToBase64(dataUrl) {
    const idx = dataUrl.indexOf(',');
    return idx !== -1 ? dataUrl.slice(idx + 1) : dataUrl;
}

function llmaDataUrlMediaType(dataUrl) {
    const match = dataUrl.match(/^data:([^;]+);/);
    return match ? match[1] : 'image/jpeg';
}

// -- Debounce --
function llmaDebounce(fn, delay) {
    let timer;
    return (...args) => {
        clearTimeout(timer);
        timer = setTimeout(() => fn(...args), delay);
    };
}

// -- Download Helper --
function llmaDownloadFile(filename, content, mimeType) {
    const blob = new Blob([content], { type: mimeType });
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement('a');
    a.href     = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
}

// -- Default Settings --
const LLMA_DEFAULT_SETTINGS = {
    // Server-canonical key is "parameters" (see C# SettingsService.DefaultSettings) — must match, or the
    // General-tab defaults silently don't apply to requests.
    parameters: {
        temperature:        0.8,
        maxTokens:          2048,
        topP:               0.9,
        seed:               -1,
        maxContextMessages: 0,
        imageMaxDimension:  1536,
        stream:             true,
    },
    ui: {
        markdownEnabled: true,
        enterToSend:     true,
        showTokens:      true,
    },
    // Floating Companion overlay (Clippy-style helper). Defaults to OFF — opt-in.
    companion: {
        enabled: false,
        // Empty = follow whichever assistant the user has marked active. Otherwise an explicit id.
        personaId: '',
        corner: 'bottom-right',
        offsetX: 24,
        offsetY: 24,
        opacity: 0.95,
        expanded: false,
        buttons: {
            ask: true,
            critique_last_image: true,
            help_with_prompt: true,
            suggest_preset: true,
            explain_feature: true,
            daily_tip: true,
        },
        chatter: {
            quietMode: false,
            greeting: true,
            reactions: false,
            idle: false,
            idleMinutes: 8,
            maxPerSession: 5,
            quietHours: false,
            quietStart: 22,
            quietEnd: 8,
        },
    },
    currentModel: null,
};

function llmaMergeSettings(defaults, stored) {
    const result = { ...defaults };
    for (const key of Object.keys(stored)) {
        if (typeof stored[key] === 'object' && !Array.isArray(stored[key]) && stored[key] !== null
            && typeof defaults[key] === 'object') {
            result[key] = llmaMergeSettings(defaults[key], stored[key]);
        } else {
            result[key] = stored[key];
        }
    }
    return result;
}

// -- Element Helpers --
function llmaSetEl(id, value, type = 'value') {
    const el = document.getElementById(id);
    if (!el) return;
    if (type === 'text') {
        const rounded = typeof value === 'number' ? +value.toFixed(2) : value;
        el.textContent = String(rounded);
    } else {
        el.value = value;
    }
}

function llmaSetElChecked(id, checked) {
    const el = document.getElementById(id);
    if (el) el.checked = checked;
}

/**
 * Generic action-button factory: a labeled <button> with a click handler.
 * Shared across message rows, attachment actions, etc.
 * @param {string} text - Button label.
 * @param {Function} onClick - Click handler.
 * @param {string} [className='llma-msg-action-btn'] - CSS class.
 * @returns {HTMLButtonElement}
 */
function llmaCreateActionBtn(text, onClick, className = 'llma-msg-action-btn') {
    const btn = document.createElement('button');
    btn.className = className;
    btn.textContent = text;
    btn.addEventListener('click', onClick);
    return btn;
}
