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
    isGenerating:      false,
    abortController:   null,
    attachedImage:     null,
    markdownEnabled:   true,
    enterToSend:       true,
    showTokens:        true,
    assets:            [],
    activeAssetId:     null,
    userProfile:       null,
};

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

async function llmaLoadCdnLibs() {
    // Core libs (required for markdown rendering)
    llmaLoadStyle('https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/styles/github-dark.min.css');
    await Promise.all([
        llmaLoadScript('https://cdnjs.cloudflare.com/ajax/libs/marked/15.0.11/marked.min.js'),
        llmaLoadScript('https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/highlight.min.js'),
        llmaLoadScript('https://cdnjs.cloudflare.com/ajax/libs/dompurify/3.2.4/purify.min.js'),
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
    // Enhanced libs (KaTeX + Mermaid) — loaded async, non-blocking
    llmaLoadStyle('https://cdnjs.cloudflare.com/ajax/libs/KaTeX/0.16.21/katex.min.css');
    llmaLoadScript('https://cdnjs.cloudflare.com/ajax/libs/KaTeX/0.16.21/katex.min.js')
        .then(() => llmaLoadScript('https://cdnjs.cloudflare.com/ajax/libs/KaTeX/0.16.21/contrib/auto-render.min.js'))
        .catch(() => console.warn('[LLMAssistant] KaTeX failed to load'));
    llmaLoadScript('https://cdnjs.cloudflare.com/ajax/libs/mermaid/11.6.0/mermaid.min.js')
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
    // Protect LaTeX delimiters from markdown parsing
    const latexBlocks = [];
    let processed = text;
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
    const clean   = window.DOMPurify.sanitize(rawHtml, {
        ALLOWED_TAGS: [
            'p','br','strong','em','del','code','pre','ul','ol','li',
            'h1','h2','h3','h4','h5','h6','blockquote','hr',
            'table','thead','tbody','tr','td','th','a','img',
            'span','div','svg','path','g','rect','line','circle','text',
            'polygon','polyline','ellipse','marker','defs','clipPath','use',
            'foreignObject','tspan',
        ],
        ALLOWED_ATTR: [
            'href','class','target','rel','src','alt','title',
            'style','d','fill','stroke','stroke-width','transform',
            'viewBox','xmlns','width','height','x','y','cx','cy','r',
            'rx','ry','x1','y1','x2','y2','points','marker-end',
            'text-anchor','dominant-baseline','font-size','id',
            'clip-path','aria-label','role','tabindex',
        ],
    });

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

    // Add copy buttons to code blocks
    result = result.replace(/<pre>/g, '<pre><button class="llma-copy-code-btn" onclick="llmaCopyCode(this)">Copy</button>');

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
    llmaToastTimer = setTimeout(() => toast.remove(), 2600);
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
    defaults: {
        temperature:     0.8,
        maxTokens:       2048,
        topP:            0.9,
        topK:            40,
        repeatPenalty:   1.1,
        seed:            -1,
        contextMessages: 0,
        stream:          true,
    },
    ui: {
        markdownEnabled: true,
        enterToSend:     true,
        showTokens:      true,
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
