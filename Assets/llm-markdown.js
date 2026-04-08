/**
 * LLM Assistant - Markdown rendering pipeline
 * marked.js + highlight.js + DOMPurify
 */
(function() {
    'use strict';

    // Configure marked with highlight.js
    if (typeof marked !== 'undefined') {
        marked.setOptions({
            breaks: true,
            gfm: true,
            highlight: function(code, lang) {
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
            'div', 'sup', 'sub'
        ],
        ALLOWED_ATTR: ['href', 'class', 'target', 'rel', 'src', 'alt', 'title'],
        ALLOW_DATA_ATTR: false
    };

    /**
     * Renders raw text to safe HTML with markdown formatting and syntax highlighting.
     * @param {string} text - Raw text from LLM response
     * @returns {string} Sanitized HTML string
     */
    function renderMarkdown(text) {
        if (!text) return '';
        // Parse markdown
        let html;
        if (typeof marked !== 'undefined') {
            html = marked.parse(text);
        } else {
            // Fallback: basic escaping
            html = text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                       .replace(/\n/g, '<br>');
        }
        // Sanitize
        if (typeof DOMPurify !== 'undefined') {
            html = DOMPurify.sanitize(html, SANITIZE_CONFIG);
        }
        return html;
    }

    /**
     * Renders markdown into a target element, adding copy buttons to code blocks.
     * @param {string} text - Raw markdown text
     * @param {HTMLElement} element - Target element
     */
    function renderIntoElement(text, element) {
        element.innerHTML = renderMarkdown(text);
        // Add copy buttons to code blocks
        element.querySelectorAll('pre code').forEach(block => {
            if (block.parentElement.querySelector('.llm-copy-code')) return;
            let btn = document.createElement('button');
            btn.className = 'llm-copy-code';
            btn.textContent = 'Copy';
            btn.addEventListener('click', () => {
                navigator.clipboard.writeText(block.textContent).then(() => {
                    btn.textContent = 'Copied!';
                    setTimeout(() => { btn.textContent = 'Copy'; }, 2000);
                });
            });
            block.parentElement.style.position = 'relative';
            block.parentElement.appendChild(btn);
        });
    }

    // Export to LLM namespace
    if (!window.LLM) window.LLM = {};
    window.LLM.renderMarkdown = renderMarkdown;
    window.LLM.renderIntoElement = renderIntoElement;
})();
