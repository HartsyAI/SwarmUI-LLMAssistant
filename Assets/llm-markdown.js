/**
 * LLM Assistant - Markdown rendering pipeline
 * marked.js + highlight.js + DOMPurify + KaTeX + Mermaid
 */
(function() {
    'use strict';

    // Configure marked with highlight.js
    if (typeof marked !== 'undefined') {
        marked.setOptions({
            breaks: true,
            gfm: true,
            highlight: function(code, lang) {
                // Skip highlighting for mermaid blocks (handled separately)
                if (lang === 'mermaid') return code;
                if (typeof hljs !== 'undefined') {
                    if (lang && hljs.getLanguage(lang)) {
                        try { return hljs.highlight(code, { language: lang }).value; }
                        catch (e) { /* fall through */ }
                    }
                    try { return hljs.highlightAuto(code).value; }
                    catch (e) { /* fall through */ }
                }
                return code;
            }
        });
    }

    // Initialize Mermaid (don't auto-start, we trigger manually)
    let mermaidReady = false;
    if (typeof mermaid !== 'undefined') {
        mermaid.initialize({ startOnLoad: false, theme: 'dark' });
        mermaidReady = true;
    }

    // Allowed tags for DOMPurify sanitization
    const SANITIZE_CONFIG = {
        ALLOWED_TAGS: [
            'p', 'br', 'strong', 'em', 'b', 'i', 'u', 's', 'del',
            'code', 'pre', 'span',
            'ul', 'ol', 'li',
            'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
            'blockquote', 'hr',
            'table', 'thead', 'tbody', 'tr', 'td', 'th',
            'a', 'img',
            'div', 'sup', 'sub',
            // KaTeX output tags
            'math', 'semantics', 'mrow', 'mi', 'mo', 'mn', 'ms', 'mtext',
            'mfrac', 'msqrt', 'mroot', 'msup', 'msub', 'msubsup', 'mover',
            'munder', 'munderover', 'mtable', 'mtr', 'mtd', 'annotation',
            // SVG tags for Mermaid
            'svg', 'g', 'path', 'line', 'rect', 'circle', 'ellipse', 'polygon',
            'polyline', 'text', 'tspan', 'defs', 'marker', 'use', 'clipPath',
            'foreignObject', 'style'
        ],
        ALLOWED_ATTR: [
            'href', 'class', 'target', 'rel', 'src', 'alt', 'title',
            // KaTeX attributes
            'mathvariant', 'encoding', 'xmlns', 'display',
            // SVG attributes for Mermaid
            'viewBox', 'width', 'height', 'fill', 'stroke', 'stroke-width',
            'stroke-dasharray', 'stroke-linecap', 'stroke-linejoin',
            'd', 'x', 'y', 'x1', 'y1', 'x2', 'y2', 'cx', 'cy', 'r', 'rx', 'ry',
            'transform', 'points', 'marker-end', 'marker-start', 'text-anchor',
            'dominant-baseline', 'font-size', 'font-family', 'font-weight',
            'clip-path', 'id', 'style', 'dx', 'dy', 'opacity',
            'xmlns:xlink', 'xlink:href', 'aria-hidden', 'role',
            'data-id', 'data-node'
        ],
        ALLOW_DATA_ATTR: true
    };

    // KaTeX rendering options
    const KATEX_OPTIONS = {
        delimiters: [
            { left: '$$', right: '$$', display: true },
            { left: '\\[', right: '\\]', display: true },
            { left: '$', right: '$', display: false },
            { left: '\\(', right: '\\)', display: false }
        ],
        throwOnError: false,
        output: 'html'
    };

    /**
     * Renders raw text to safe HTML with markdown formatting and syntax highlighting.
     * @param {string} text - Raw text from LLM response
     * @returns {string} Sanitized HTML string
     */
    function renderMarkdown(text) {
        if (!text) return '';
        let html;
        if (typeof marked !== 'undefined') {
            html = marked.parse(text);
        } else {
            html = text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                       .replace(/\n/g, '<br>');
        }
        if (typeof DOMPurify !== 'undefined') {
            html = DOMPurify.sanitize(html, SANITIZE_CONFIG);
        }
        return html;
    }

    /**
     * Renders markdown into a target element with KaTeX, Mermaid, and copy buttons.
     * @param {string} text - Raw markdown text
     * @param {HTMLElement} element - Target element
     */
    function renderIntoElement(text, element) {
        element.innerHTML = renderMarkdown(text);
        // Process Mermaid code blocks: <pre><code class="language-mermaid"> → <div class="mermaid">
        if (mermaidReady) {
            processMermaidBlocks(element);
        }
        // Process KaTeX math expressions
        if (typeof renderMathInElement === 'function') {
            try {
                renderMathInElement(element, KATEX_OPTIONS);
            } catch (e) {
                // KaTeX errors are non-fatal
            }
        }
        // Add language label + copy button header to code blocks
        element.querySelectorAll('pre code').forEach(block => {
            let pre = block.parentElement;
            if (pre.querySelector('.llm-code-header')) return;
            // Extract language from class name
            let lang = '';
            for (let cls of block.className.split(/\s+/)) {
                if (cls.startsWith('language-') && cls !== 'language-undefined') {
                    lang = cls.replace('language-', '');
                    break;
                }
            }
            let header = document.createElement('div');
            header.className = 'llm-code-header';
            let langLabel = document.createElement('span');
            langLabel.className = 'llm-code-lang';
            langLabel.textContent = lang;
            let copyBtn = document.createElement('button');
            copyBtn.className = 'llm-copy-code';
            copyBtn.textContent = 'Copy';
            copyBtn.addEventListener('click', () => {
                navigator.clipboard.writeText(block.textContent).then(() => {
                    copyBtn.textContent = 'Copied!';
                    setTimeout(() => { copyBtn.textContent = 'Copy'; }, 2000);
                });
            });
            header.appendChild(langLabel);
            header.appendChild(copyBtn);
            pre.insertBefore(header, pre.firstChild);
        });
    }

    /**
     * Converts mermaid code blocks into rendered diagrams.
     */
    let mermaidCounter = 0;
    function processMermaidBlocks(element) {
        let blocks = element.querySelectorAll('pre code.language-mermaid, pre code.hljs.language-mermaid');
        blocks.forEach(block => {
            let pre = block.parentElement;
            let code = block.textContent;
            let container = document.createElement('div');
            container.className = 'llm-mermaid-container';
            let diagramDiv = document.createElement('div');
            diagramDiv.className = 'mermaid';
            diagramDiv.id = `llm-mermaid-${++mermaidCounter}`;
            diagramDiv.textContent = code;
            container.appendChild(diagramDiv);
            pre.replaceWith(container);
        });
        // Trigger mermaid rendering on all new blocks
        if (blocks.length > 0) {
            try {
                mermaid.run({ querySelector: '.llm-mermaid-container .mermaid' });
            } catch (e) {
                console.warn('[LLMAssistant] Mermaid render error:', e);
            }
        }
    }

    // Export to LLM namespace
    if (!window.LLM) window.LLM = {};
    window.LLM.renderMarkdown = renderMarkdown;
    window.LLM.renderIntoElement = renderIntoElement;
})();
