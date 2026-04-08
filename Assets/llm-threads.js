/**
 * LLM Assistant - Thread sidebar manager
 */
(function() {
    'use strict';

    const Threads = {
        threads: [],

        init() {
            this.bindEvents();
            this.refreshList();
        },

        bindEvents() {
            let newBtn = document.getElementById('llm-new-thread-btn');
            if (newBtn) newBtn.addEventListener('click', () => this.createNew());
            let toggleBtn = document.getElementById('llm-sidebar-toggle');
            if (toggleBtn) {
                toggleBtn.addEventListener('click', () => {
                    let sidebar = document.getElementById('llm-sidebar');
                    if (sidebar) sidebar.classList.toggle('collapsed');
                });
            }
            let search = document.getElementById('llm-thread-search');
            if (search) {
                search.addEventListener('input', () => this.filterList(search.value));
            }
        },

        async refreshList() {
            try {
                let resp = await LLM.APIClient.getThreads();
                this.threads = resp.threads || [];
                this.renderList();
            } catch (ex) {
                console.error('[LLMAssistant] Failed to load threads:', ex);
            }
        },

        renderList(filter) {
            let container = document.getElementById('llm-thread-list');
            if (!container) return;
            container.innerHTML = '';
            let filtered = this.threads;
            if (filter) {
                let lowerFilter = filter.toLowerCase();
                filtered = filtered.filter(t =>
                    (t.title || '').toLowerCase().includes(lowerFilter)
                );
            }
            if (filtered.length === 0) {
                container.innerHTML = '<div class="llm-thread-empty">No threads yet</div>';
                return;
            }
            for (let thread of filtered) {
                let item = document.createElement('div');
                item.className = 'llm-thread-item';
                if (thread.id === LLM.currentThreadId) {
                    item.classList.add('active');
                }
                item.setAttribute('data-thread-id', thread.id);
                let title = document.createElement('span');
                title.className = 'llm-thread-item-title';
                title.textContent = thread.title || 'Untitled';
                let meta = document.createElement('span');
                meta.className = 'llm-thread-item-meta';
                meta.textContent = this.formatDate(thread.updatedAt);
                let delBtn = document.createElement('button');
                delBtn.className = 'llm-thread-delete';
                delBtn.textContent = '\u00d7';
                delBtn.title = 'Delete thread';
                delBtn.addEventListener('click', e => {
                    e.stopPropagation();
                    this.deleteThread(thread.id);
                });
                item.appendChild(title);
                item.appendChild(meta);
                item.appendChild(delBtn);
                item.addEventListener('click', () => this.switchTo(thread.id));
                container.appendChild(item);
            }
        },

        filterList(text) {
            this.renderList(text);
        },

        async switchTo(threadId) {
            try {
                let resp = await LLM.APIClient.getThread(threadId);
                if (resp.thread) {
                    LLM.currentThreadId = threadId;
                    let titleEl = document.getElementById('llm-thread-title');
                    if (titleEl) titleEl.textContent = resp.thread.title || 'Untitled';
                    LLM.Chat.loadMessages(resp.thread.messages || []);
                    this.renderList(); // Update active state
                }
            } catch (ex) {
                console.error('[LLMAssistant] Failed to load thread:', ex);
            }
        },

        createNew() {
            LLM.currentThreadId = null;
            LLM.Chat.clearMessages();
            let titleEl = document.getElementById('llm-thread-title');
            if (titleEl) titleEl.textContent = 'New Thread';
            this.renderList(); // Clear active state
        },

        async deleteThread(threadId) {
            if (!confirm('Delete this thread?')) return;
            try {
                await LLM.APIClient.deleteThread(threadId);
                if (LLM.currentThreadId === threadId) {
                    this.createNew();
                }
                await this.refreshList();
            } catch (ex) {
                console.error('[LLMAssistant] Failed to delete thread:', ex);
            }
        },

        formatDate(isoString) {
            if (!isoString) return '';
            let date = new Date(isoString);
            let now = new Date();
            let diff = now - date;
            if (diff < 60000) return 'Just now';
            if (diff < 3600000) return Math.floor(diff / 60000) + 'm ago';
            if (diff < 86400000) return Math.floor(diff / 3600000) + 'h ago';
            if (diff < 604800000) return Math.floor(diff / 86400000) + 'd ago';
            return date.toLocaleDateString();
        }
    };

    if (!window.LLM) window.LLM = {};
    window.LLM.Threads = Threads;
})();
