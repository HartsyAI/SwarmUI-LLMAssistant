/* ================================================================
   LLM Assistant - assets.js
   Claude-style artifact/asset system.
   - Pluggable asset-type registry
   - Fenced-code-block + tool-result extractor
   - Inline cards in chat bubbles
   - Sidebar stack of cards in the right panel
   - Full-size viewer modal (in-tab overlay)
   Loaded after utils.js, before chat.js/threads.js/llmassistant.js.
   ================================================================ */

'use strict';

// ── State additions (utils.js also initializes these; this is a safety net) ──
if (typeof LLMAState !== 'undefined') {
    if (!Array.isArray(LLMAState.assets)) LLMAState.assets = [];
    if (LLMAState.activeAssetId === undefined) LLMAState.activeAssetId = null;
}

// ── Registry ─────────────────────────────────────────────────────
const LLMA_ASSET_TYPES = {};

function llmaRegisterAssetType(def) {
    if (!def || !def.type) return;
    LLMA_ASSET_TYPES[def.type] = def;
}

function llmaAssetTypeDef(type) {
    return LLMA_ASSET_TYPES[type] || LLMA_ASSET_TYPES.text;
}

// ── Language → type / extension maps ─────────────────────────────
const LLMA_LANG_TO_TYPE = {
    html: 'html', htm: 'html', xhtml: 'html',
    md: 'markdown', markdown: 'markdown',
    svg: 'svg',
    mermaid: 'mermaid',
    json: 'json',
    // everything else that has a language is treated as 'code'
};

const LLMA_LANG_TO_EXT = {
    javascript: 'js', js: 'js', jsx: 'jsx',
    typescript: 'ts', ts: 'ts', tsx: 'tsx',
    python: 'py', py: 'py',
    'c++': 'cpp', cpp: 'cpp', c: 'c', h: 'h', hpp: 'hpp',
    csharp: 'cs', cs: 'cs',
    java: 'java', kotlin: 'kt', swift: 'swift',
    go: 'go', rust: 'rs', rs: 'rs',
    ruby: 'rb', rb: 'rb',
    php: 'php', lua: 'lua', perl: 'pl',
    bash: 'sh', sh: 'sh', shell: 'sh', zsh: 'sh',
    powershell: 'ps1', ps1: 'ps1', bat: 'bat',
    sql: 'sql', yaml: 'yaml', yml: 'yaml', toml: 'toml', ini: 'ini',
    xml: 'xml', css: 'css', scss: 'scss', less: 'less',
    dockerfile: 'dockerfile', makefile: 'makefile',
};

const LLMA_LANG_TO_LABEL = {
    js: 'JavaScript', jsx: 'JSX', ts: 'TypeScript', tsx: 'TSX',
    py: 'Python', cpp: 'C++', cs: 'C#', java: 'Java',
    go: 'Go', rs: 'Rust', rb: 'Ruby', php: 'PHP', lua: 'Lua',
    sh: 'Shell', ps1: 'PowerShell', sql: 'SQL',
    yaml: 'YAML', toml: 'TOML', xml: 'XML',
    css: 'CSS', scss: 'SCSS',
};

function llmaLangLabel(lang) {
    if (!lang) return 'Code';
    const key = lang.toLowerCase();
    if (LLMA_LANG_TO_LABEL[key]) return LLMA_LANG_TO_LABEL[key];
    return lang.charAt(0).toUpperCase() + lang.slice(1);
}

function llmaLangExt(lang) {
    if (!lang) return 'txt';
    return LLMA_LANG_TO_EXT[lang.toLowerCase()] || lang.toLowerCase();
}

// ── Utilities ────────────────────────────────────────────────────
function llmaByteSize(str) {
    try { return new Blob([str || '']).size; }
    catch { return (str || '').length; }
}

function llmaFormatBytes(n) {
    if (n == null) return '';
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

function llmaLineCount(str) {
    if (!str) return 0;
    return str.split('\n').length;
}

// ── Asset construction ───────────────────────────────────────────
function llmaGuessFilename(content, lang, type) {
    if (!content) return null;
    const head = content.split('\n').slice(0, 6).join('\n');

    // HTML <title>
    if (type === 'html') {
        const m = head.match(/<title[^>]*>([^<]+)<\/title>/i);
        if (m) return m[1].trim().replace(/\s+/g, '_') + '.html';
    }
    // YAML front-matter title
    const fm = content.match(/^---\s*\n([\s\S]+?)\n---/);
    if (fm) {
        const t = fm[1].match(/title:\s*["']?([^"'\n]+)["']?/i);
        if (t) return t[1].trim().replace(/\s+/g, '_') + '.md';
    }
    // Explicit file comment
    const commentPatterns = [
        /^\s*<!--\s*file:\s*([^\s>]+)\s*-->/im,
        /^\s*\/\/\s*file:\s*(\S+)/im,
        /^\s*#\s*file:\s*(\S+)/im,
        /^\s*\/\*\s*file:\s*(\S+)\s*\*\//im,
    ];
    for (const re of commentPatterns) {
        const m = content.match(re);
        if (m) return m[1].trim();
    }
    // Markdown first H1
    if (type === 'markdown') {
        const h = content.match(/^#\s+(.+)$/m);
        if (h) return h[1].trim().slice(0, 40).replace(/[^\w\s.-]/g, '').replace(/\s+/g, '_') + '.md';
    }
    return null;
}

function llmaBuildAsset({ id, type, language, content, msgId, source, toolId, idx, title }) {
    const resolvedType = type || 'text';
    const def = llmaAssetTypeDef(resolvedType);
    const guessed = title || llmaGuessFilename(content, language, resolvedType);
    const ext = resolvedType === 'markdown' ? 'md'
              : resolvedType === 'html' ? 'html'
              : resolvedType === 'svg' ? 'svg'
              : resolvedType === 'json' ? 'json'
              : resolvedType === 'mermaid' ? 'mmd'
              : resolvedType === 'code' ? llmaLangExt(language)
              : resolvedType === 'image' ? 'png'
              : 'txt';
    const finalTitle = guessed || `Untitled-${(idx ?? 0) + 1}.${ext}`;
    return {
        id,
        type: resolvedType,
        language: language || null,
        title: finalTitle,
        icon: def.icon || '\u{1F4C4}',
        content: content || '',
        meta: {
            msgId,
            source: source || 'assistant',
            toolId: toolId || null,
            size: llmaByteSize(content),
            lines: resolvedType === 'code' || resolvedType === 'text' ? llmaLineCount(content) : undefined,
            createdAt: new Date().toISOString(),
        },
    };
}

// ── Fenced code-block extractor ──────────────────────────────────
// Returns { assets: [...], tokenizedMarkdown: "..." }
// The tokenized markdown has each extracted fence replaced with a bare-line token
// `LLMA_ASSET_TOKEN_{id}` which survives marked + DOMPurify as plain text.
function llmaExtractAssetsFromMarkdown(raw, msgId) {
    if (!raw) return { assets: [], tokenizedMarkdown: raw || '' };
    const assets = [];
    // Preserve mermaid — utils.js handles that pipeline separately; we do NOT touch mermaid fences.
    // Pattern: ``` optional-lang \n body \n ```
    const fenceRe = /```([^\s`]*)\s*\n([\s\S]*?)```/g;
    let idx = 0;
    const tokenized = raw.replace(fenceRe, (match, lang, body) => {
        const langLower = (lang || '').toLowerCase();
        if (langLower === 'mermaid') return match; // leave to mermaid pipeline
        const trimmedBody = body.replace(/\n$/, '');
        // Skip very short unlabeled blocks (likely an inline snippet)
        if (!langLower && llmaLineCount(trimmedBody) < 4) return match;

        let type = LLMA_LANG_TO_TYPE[langLower];
        if (!type) type = langLower ? 'code' : 'text';

        const assetId = `${msgId}-blk-${idx}`;
        const asset = llmaBuildAsset({
            id: assetId,
            type,
            language: langLower || null,
            content: trimmedBody,
            msgId,
            source: 'assistant',
            idx,
        });
        assets.push(asset);
        idx++;
        return `\n\nLLMA_ASSET_TOKEN_${assetId}\n\n`;
    });
    return { assets, tokenizedMarkdown: tokenized };
}

// ── Tool result → asset promotion ────────────────────────────────
function llmaExtractAssetsFromToolCalls(toolCalls, msgId) {
    if (!Array.isArray(toolCalls)) return [];
    const out = [];
    toolCalls.forEach((tc, i) => {
        if (!tc || !tc.result) return;
        const r = tc.result;
        if (r.error || r.success === false) return;
        const name = tc.name || '';
        const baseId = `${msgId}-tool-${tc.id || i}`;
        if (name === 'generate_image' && r.imageUrl) {
            out.push(llmaBuildAsset({
                id: baseId,
                type: 'image',
                content: r.imageUrl,
                msgId,
                source: 'tool',
                toolId: tc.id || null,
                idx: i,
                title: r.prompt ? (r.prompt.slice(0, 40).replace(/\s+/g, '_') + '.png') : null,
            }));
        } else if (name === 'file_read' && typeof r.content === 'string') {
            const path = r.path || '';
            const ext = (path.match(/\.([a-z0-9]+)$/i) || [])[1]?.toLowerCase() || '';
            const lang = ext;
            const type = LLMA_LANG_TO_TYPE[ext] || (ext ? 'code' : 'text');
            out.push(llmaBuildAsset({
                id: baseId,
                type,
                language: lang || null,
                content: r.content,
                msgId,
                source: 'tool',
                toolId: tc.id || null,
                idx: i,
                title: path ? path.split(/[\\/]/).pop() : null,
            }));
        }
    });
    return out;
}

// ── Rebuild all assets for the active thread (deterministic) ─────
function llmaRebuildAssetsForThread() {
    if (typeof LLMAState === 'undefined') return;
    const all = [];
    for (const msg of LLMAState.messages || []) {
        if (msg.role !== 'assistant') continue;
        const raw = msg.rawContent || msg.content || '';
        if (raw) {
            const { assets } = llmaExtractAssetsFromMarkdown(raw, msg.id);
            all.push(...assets);
        }
        if (Array.isArray(msg.toolCalls) && msg.toolCalls.length > 0) {
            all.push(...llmaExtractAssetsFromToolCalls(msg.toolCalls, msg.id));
        }
    }
    LLMAState.assets = all;
    llmaRenderAssetSidebar();
}

// ── Render assistant content (markdown + inline asset cards) ─────
function llmaRenderAssistantContent(raw, msgId) {
    if (!raw) return '';
    const { assets, tokenizedMarkdown } = llmaExtractAssetsFromMarkdown(raw, msgId);
    let html = llmaRenderMarkdown(tokenizedMarkdown);
    // Replace token placeholders with asset cards
    for (const asset of assets) {
        const cardHtml = llmaRenderAssetCardHtml(asset, { inline: true });
        // marked may wrap the bare token in <p>…</p>; strip that wrapper as well
        const tokenRe = new RegExp(`(<p>\\s*)?LLMA_ASSET_TOKEN_${llmaEscapeRegExp(asset.id)}(\\s*</p>)?`, 'g');
        html = html.replace(tokenRe, cardHtml);
    }
    return html;
}

function llmaEscapeRegExp(s) {
    return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// ── Asset card HTML (inline + sidebar share the same structure) ──
function llmaRenderAssetCardHtml(asset, { inline = false, compact = false } = {}) {
    const def = llmaAssetTypeDef(asset.type);
    const icon = def.icon || '\u{1F4C4}';
    const typeLabel = asset.type === 'code'
        ? `Code \u00b7 ${llmaLangLabel(asset.language)}`
        : (def.label || asset.type.charAt(0).toUpperCase() + asset.type.slice(1));
    const sizeStr = asset.meta?.size != null ? llmaFormatBytes(asset.meta.size) : '';
    const lineStr = asset.meta?.lines ? ` \u00b7 ${asset.meta.lines} lines` : '';
    const subtitle = `${typeLabel}${sizeStr ? ' \u00b7 ' + sizeStr : ''}${lineStr}`;
    const cls = `llma-asset-card${inline ? ' inline' : ''}${compact ? ' compact' : ''}`;

    // Thumbnail per type
    let thumb = `<span class="llma-asset-icon">${icon}</span>`;
    if (asset.type === 'image' && asset.content) {
        thumb = `<span class="llma-asset-thumb"><img src="${llmaEscapeHtml(asset.content)}" alt=""></span>`;
    }

    return `<div class="${cls}" data-asset-id="${llmaEscapeHtml(asset.id)}" role="button" tabindex="0">
        ${thumb}
        <div class="llma-asset-card-body">
            <div class="llma-asset-card-title">${llmaEscapeHtml(asset.title)}</div>
            <div class="llma-asset-card-sub">${llmaEscapeHtml(subtitle)}</div>
        </div>
        <button class="llma-asset-card-open" data-asset-open="${llmaEscapeHtml(asset.id)}" title="Open">Open</button>
    </div>`;
}

// ── Sidebar renderer ─────────────────────────────────────────────
function llmaRenderAssetSidebar() {
    const list = document.getElementById('llma-asset-list');
    const countEl = document.getElementById('llma-asset-count');
    if (!list) return;
    const assets = (LLMAState.assets || []);
    if (countEl) countEl.textContent = String(assets.length);

    if (assets.length === 0) {
        list.innerHTML = '<div class="llma-asset-empty">No assets yet.<br>Assets created in this chat will appear here.</div>';
        return;
    }
    // Newest first
    const ordered = assets.slice().reverse();
    list.innerHTML = ordered.map(a => llmaRenderAssetCardHtml(a, { compact: true })).join('');
}

// ── Viewer (full-size modal) ─────────────────────────────────────
function llmaOpenAsset(id) {
    const asset = (LLMAState.assets || []).find(a => a.id === id);
    if (!asset) return;
    LLMAState.activeAssetId = id;
    const overlay = document.getElementById('llma-asset-overlay');
    const titleEl = document.getElementById('llma-asset-modal-title');
    const badgeEl = document.getElementById('llma-asset-modal-badge');
    const iconEl  = document.getElementById('llma-asset-modal-icon');
    const bodyEl  = document.getElementById('llma-asset-modal-body');
    if (!overlay || !bodyEl) return;

    const def = llmaAssetTypeDef(asset.type);
    if (titleEl) titleEl.textContent = asset.title;
    if (iconEl) iconEl.textContent = def.icon || '\u{1F4C4}';
    if (badgeEl) {
        badgeEl.textContent = asset.type === 'code'
            ? llmaLangLabel(asset.language)
            : (def.label || asset.type);
    }

    bodyEl.innerHTML = '';
    try {
        const viewEl = def.renderViewer ? def.renderViewer(asset) : llmaRenderViewerText(asset);
        if (viewEl) bodyEl.appendChild(viewEl);
    } catch (ex) {
        bodyEl.innerHTML = `<div class="llma-asset-error">Failed to render: ${llmaEscapeHtml(ex.message || String(ex))}</div>`;
    }

    // Show/hide "Use as Prompt" for text-like assets
    const usePromptBtn = document.getElementById('llma-asset-use-prompt');
    if (usePromptBtn) {
        const canUse = asset.type !== 'image';
        usePromptBtn.style.display = canUse ? '' : 'none';
    }
    // Show/hide "Use as Init" for image assets
    const useInitBtn = document.getElementById('llma-asset-use-init');
    if (useInitBtn) {
        useInitBtn.style.display = asset.type === 'image' ? '' : 'none';
    }

    overlay.style.display = '';
}

function llmaCloseAssetViewer() {
    const overlay = document.getElementById('llma-asset-overlay');
    if (overlay) overlay.style.display = 'none';
    LLMAState.activeAssetId = null;
}

function llmaCurrentAsset() {
    return (LLMAState.assets || []).find(a => a.id === LLMAState.activeAssetId) || null;
}

// ── Default text renderer (fallback viewer) ──────────────────────
function llmaRenderViewerText(asset) {
    const pre = document.createElement('pre');
    pre.className = 'llma-asset-view-text';
    pre.textContent = asset.content || '';
    return pre;
}

// ── Copy / Download helpers ──────────────────────────────────────
function llmaAssetCopy(asset) {
    if (!asset) return;
    const def = llmaAssetTypeDef(asset.type);
    const payload = def.copyPayload ? def.copyPayload(asset) : asset.content;
    if (payload == null) return;
    navigator.clipboard.writeText(String(payload))
        .then(() => llmaShowToast('Copied!', 'success'))
        .catch(() => llmaShowToast('Copy failed', 'error'));
}

function llmaAssetDownload(asset) {
    if (!asset) return;
    const def = llmaAssetTypeDef(asset.type);
    let dl;
    if (def.download) {
        dl = def.download(asset);
    } else {
        dl = { filename: asset.title, mime: 'text/plain', content: asset.content || '' };
    }
    llmaDownloadFile(dl.filename || asset.title, dl.content, dl.mime || 'text/plain');
}

// ── Event delegation + viewer chrome wiring ──────────────────────
function llmaSetupAssets() {
    const container = document.getElementById('llma-container');
    if (!container) return;

    // Delegated clicks on asset cards
    container.addEventListener('click', (e) => {
        const openBtn = e.target.closest('[data-asset-open]');
        if (openBtn) {
            e.preventDefault();
            e.stopPropagation();
            llmaOpenAsset(openBtn.getAttribute('data-asset-open'));
            return;
        }
        const card = e.target.closest('.llma-asset-card');
        if (card) {
            e.preventDefault();
            const id = card.getAttribute('data-asset-id');
            if (id) llmaOpenAsset(id);
        }
    });
    // Keyboard activation for cards
    container.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        const card = e.target.closest?.('.llma-asset-card');
        if (card) {
            e.preventDefault();
            const id = card.getAttribute('data-asset-id');
            if (id) llmaOpenAsset(id);
        }
    });

    // Viewer chrome
    document.getElementById('llma-asset-close')?.addEventListener('click', llmaCloseAssetViewer);
    document.getElementById('llma-asset-copy')?.addEventListener('click', () => {
        llmaAssetCopy(llmaCurrentAsset());
    });
    document.getElementById('llma-asset-download')?.addEventListener('click', () => {
        llmaAssetDownload(llmaCurrentAsset());
    });
    document.getElementById('llma-asset-use-prompt')?.addEventListener('click', () => {
        const a = llmaCurrentAsset();
        if (a && typeof llmaSendToPromptBox === 'function') {
            llmaSendToPromptBox(a.content || '');
            llmaShowToast('Sent to prompt', 'success');
        }
    });
    document.getElementById('llma-asset-use-init')?.addEventListener('click', () => {
        const a = llmaCurrentAsset();
        if (a && a.type === 'image' && typeof setCurrentImage === 'function') {
            setCurrentImage(a.content);
            llmaShowToast('Sent as init image', 'success');
        }
    });
    // Click outside closes
    const overlay = document.getElementById('llma-asset-overlay');
    if (overlay) {
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) llmaCloseAssetViewer();
        });
    }
}

// ════════════════════════════════════════════════════════════════
//   Built-in asset types
// ════════════════════════════════════════════════════════════════

// ── Code ─────────────────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'code',
    icon: '\u{1F4BB}',
    label: 'Code',
    renderViewer: (asset) => {
        const wrap = document.createElement('div');
        wrap.className = 'llma-asset-view-code';
        const pre = document.createElement('pre');
        const code = document.createElement('code');
        const lang = (asset.language || '').toLowerCase();
        if (window.hljs) {
            try {
                if (lang && window.hljs.getLanguage(lang)) {
                    code.innerHTML = window.hljs.highlight(asset.content || '', { language: lang }).value;
                } else {
                    code.innerHTML = window.hljs.highlightAuto(asset.content || '').value;
                }
                code.className = `hljs language-${lang}`;
            } catch {
                code.textContent = asset.content || '';
            }
        } else {
            code.textContent = asset.content || '';
        }
        pre.appendChild(code);
        wrap.appendChild(pre);
        return wrap;
    },
    download: (asset) => ({
        filename: asset.title,
        mime: 'text/plain',
        content: asset.content || '',
    }),
});

// ── Markdown ─────────────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'markdown',
    icon: '\u{1F4DD}',
    label: 'Markdown',
    renderViewer: (asset) => {
        const wrap = document.createElement('div');
        wrap.className = 'llma-asset-view-markdown';
        wrap.innerHTML = llmaRenderMarkdown(asset.content || '');
        llmaPostRenderMermaid(wrap);
        return wrap;
    },
    download: (asset) => ({
        filename: asset.title.endsWith('.md') ? asset.title : asset.title + '.md',
        mime: 'text/markdown',
        content: asset.content || '',
    }),
});

// ── HTML ─────────────────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'html',
    icon: '\u{1F310}',
    label: 'HTML',
    renderViewer: (asset) => {
        const wrap = document.createElement('div');
        wrap.className = 'llma-asset-view-html';

        // Tab bar: Preview | Source
        const tabs = document.createElement('div');
        tabs.className = 'llma-asset-view-tabs';
        const previewTab = document.createElement('button');
        previewTab.className = 'llma-asset-view-tab active';
        previewTab.textContent = 'Preview';
        const sourceTab = document.createElement('button');
        sourceTab.className = 'llma-asset-view-tab';
        sourceTab.textContent = 'Source';
        const scriptsBtn = document.createElement('button');
        scriptsBtn.className = 'llma-asset-view-tab llma-asset-scripts-btn';
        scriptsBtn.textContent = 'Enable scripts';
        scriptsBtn.title = 'Scripts are sandboxed; click to opt in (still sandboxed from the host)';
        tabs.appendChild(previewTab);
        tabs.appendChild(sourceTab);
        tabs.appendChild(scriptsBtn);
        wrap.appendChild(tabs);

        // Preview pane
        const previewPane = document.createElement('div');
        previewPane.className = 'llma-asset-view-pane preview';
        const iframe = document.createElement('iframe');
        iframe.className = 'llma-asset-iframe';
        iframe.sandbox = 'allow-same-origin';
        iframe.srcdoc = asset.content || '';
        previewPane.appendChild(iframe);
        wrap.appendChild(previewPane);

        // Source pane
        const sourcePane = document.createElement('div');
        sourcePane.className = 'llma-asset-view-pane source';
        sourcePane.style.display = 'none';
        const pre = document.createElement('pre');
        const code = document.createElement('code');
        code.className = 'hljs language-html';
        if (window.hljs) {
            try { code.innerHTML = window.hljs.highlight(asset.content || '', { language: 'html' }).value; }
            catch { code.textContent = asset.content || ''; }
        } else {
            code.textContent = asset.content || '';
        }
        pre.appendChild(code);
        sourcePane.appendChild(pre);
        wrap.appendChild(sourcePane);

        previewTab.addEventListener('click', () => {
            previewTab.classList.add('active');
            sourceTab.classList.remove('active');
            previewPane.style.display = '';
            sourcePane.style.display = 'none';
        });
        sourceTab.addEventListener('click', () => {
            sourceTab.classList.add('active');
            previewTab.classList.remove('active');
            previewPane.style.display = 'none';
            sourcePane.style.display = '';
        });
        scriptsBtn.addEventListener('click', () => {
            const enabled = iframe.sandbox.contains('allow-scripts');
            if (enabled) {
                iframe.sandbox = 'allow-same-origin';
                scriptsBtn.textContent = 'Enable scripts';
                scriptsBtn.classList.remove('active');
            } else {
                iframe.sandbox = 'allow-scripts';
                scriptsBtn.textContent = 'Disable scripts';
                scriptsBtn.classList.add('active');
            }
            // Force reload of srcdoc to apply new sandbox
            const src = iframe.srcdoc;
            iframe.srcdoc = '';
            setTimeout(() => { iframe.srcdoc = src; }, 0);
        });

        return wrap;
    },
    download: (asset) => ({
        filename: asset.title.endsWith('.html') ? asset.title : asset.title + '.html',
        mime: 'text/html',
        content: asset.content || '',
    }),
});

// ── SVG ──────────────────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'svg',
    icon: '\u{1F5BC}',
    label: 'SVG',
    renderViewer: (asset) => {
        const wrap = document.createElement('div');
        wrap.className = 'llma-asset-view-svg';
        if (window.DOMPurify) {
            wrap.innerHTML = window.DOMPurify.sanitize(asset.content || '', { USE_PROFILES: { svg: true, svgFilters: true } });
        } else {
            wrap.textContent = asset.content || '';
        }
        return wrap;
    },
    download: (asset) => ({
        filename: asset.title.endsWith('.svg') ? asset.title : asset.title + '.svg',
        mime: 'image/svg+xml',
        content: asset.content || '',
    }),
});

// ── Mermaid ──────────────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'mermaid',
    icon: '\u{1F4CA}',
    label: 'Diagram',
    renderViewer: (asset) => {
        const wrap = document.createElement('div');
        wrap.className = 'llma-asset-view-mermaid';
        const block = document.createElement('div');
        block.className = 'llma-mermaid-block';
        block.id = `llma-asset-mermaid-${++llmaMermaidCounter}`;
        block.dataset.mermaid = asset.content || '';
        block.innerHTML = `<pre><code>${llmaEscapeHtml(asset.content || '')}</code></pre>`;
        wrap.appendChild(block);
        setTimeout(() => llmaPostRenderMermaid(wrap), 0);
        return wrap;
    },
    download: (asset) => ({
        filename: asset.title.endsWith('.mmd') ? asset.title : asset.title + '.mmd',
        mime: 'text/plain',
        content: asset.content || '',
    }),
});

// ── JSON ─────────────────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'json',
    icon: '\u{1F4E6}',
    label: 'JSON',
    renderViewer: (asset) => {
        const wrap = document.createElement('div');
        wrap.className = 'llma-asset-view-json';
        let obj;
        try { obj = JSON.parse(asset.content || 'null'); }
        catch (ex) {
            wrap.innerHTML = `<div class="llma-asset-error">Invalid JSON: ${llmaEscapeHtml(ex.message)}</div>
                <pre>${llmaEscapeHtml(asset.content || '')}</pre>`;
            return wrap;
        }
        wrap.appendChild(llmaRenderJsonTree(obj));
        return wrap;
    },
    download: (asset) => ({
        filename: asset.title.endsWith('.json') ? asset.title : asset.title + '.json',
        mime: 'application/json',
        content: asset.content || '',
    }),
});

function llmaRenderJsonTree(value, key) {
    const node = document.createElement('div');
    node.className = 'llma-json-node';
    const keyHtml = key !== undefined ? `<span class="llma-json-key">"${llmaEscapeHtml(String(key))}"</span>: ` : '';
    if (value === null) {
        node.innerHTML = `${keyHtml}<span class="llma-json-null">null</span>`;
    } else if (typeof value === 'boolean') {
        node.innerHTML = `${keyHtml}<span class="llma-json-bool">${value}</span>`;
    } else if (typeof value === 'number') {
        node.innerHTML = `${keyHtml}<span class="llma-json-num">${value}</span>`;
    } else if (typeof value === 'string') {
        node.innerHTML = `${keyHtml}<span class="llma-json-str">"${llmaEscapeHtml(value)}"</span>`;
    } else if (Array.isArray(value)) {
        const head = document.createElement('div');
        head.className = 'llma-json-head';
        head.innerHTML = `<button class="llma-json-toggle">\u25be</button>${keyHtml}<span class="llma-json-bracket">[</span> <span class="llma-json-count">${value.length} items</span>`;
        node.appendChild(head);
        const body = document.createElement('div');
        body.className = 'llma-json-body';
        value.forEach((v, i) => body.appendChild(llmaRenderJsonTree(v, i)));
        node.appendChild(body);
        const tail = document.createElement('div');
        tail.className = 'llma-json-tail';
        tail.innerHTML = '<span class="llma-json-bracket">]</span>';
        node.appendChild(tail);
        head.querySelector('.llma-json-toggle').addEventListener('click', (e) => {
            e.stopPropagation();
            node.classList.toggle('collapsed');
        });
    } else if (typeof value === 'object') {
        const keys = Object.keys(value);
        const head = document.createElement('div');
        head.className = 'llma-json-head';
        head.innerHTML = `<button class="llma-json-toggle">\u25be</button>${keyHtml}<span class="llma-json-bracket">{</span> <span class="llma-json-count">${keys.length} keys</span>`;
        node.appendChild(head);
        const body = document.createElement('div');
        body.className = 'llma-json-body';
        keys.forEach(k => body.appendChild(llmaRenderJsonTree(value[k], k)));
        node.appendChild(body);
        const tail = document.createElement('div');
        tail.className = 'llma-json-tail';
        tail.innerHTML = '<span class="llma-json-bracket">}</span>';
        node.appendChild(tail);
        head.querySelector('.llma-json-toggle').addEventListener('click', (e) => {
            e.stopPropagation();
            node.classList.toggle('collapsed');
        });
    }
    return node;
}

// ── Image ────────────────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'image',
    icon: '\u{1F5BC}',
    label: 'Image',
    renderViewer: (asset) => {
        const wrap = document.createElement('div');
        wrap.className = 'llma-asset-view-image';
        const img = document.createElement('img');
        img.src = asset.content || '';
        img.alt = asset.title || '';
        wrap.appendChild(img);
        return wrap;
    },
    copyPayload: (asset) => asset.content || '',
    download: (asset) => ({
        filename: asset.title,
        mime: 'image/png',
        // Images are URLs, can't be blob-downloaded directly without fetch; link-download instead
        content: asset.content || '',
    }),
});

// ── Table (GFM) ──────────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'table',
    icon: '\u{1F4CB}',
    label: 'Table',
    renderViewer: (asset) => {
        const wrap = document.createElement('div');
        wrap.className = 'llma-asset-view-table';
        wrap.innerHTML = llmaRenderMarkdown(asset.content || '');
        return wrap;
    },
    download: (asset) => ({
        filename: asset.title.endsWith('.md') ? asset.title : asset.title + '.md',
        mime: 'text/markdown',
        content: asset.content || '',
    }),
});

// ── Text (fallback) ──────────────────────────────────────────────
llmaRegisterAssetType({
    type: 'text',
    icon: '\u{1F4C4}',
    label: 'Text',
    renderViewer: (asset) => llmaRenderViewerText(asset),
    download: (asset) => ({
        filename: asset.title,
        mime: 'text/plain',
        content: asset.content || '',
    }),
});
