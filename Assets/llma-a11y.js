/* ================================================================
   LLM Assistant - llma-a11y.js
   Keyboard, focus and screen-reader support for the chat UI:
   message navigation, popovers, dialogs, tab lists and the live region.
   ================================================================ */

'use strict';

/** The elements that count as one message for keyboard navigation. A compare turn's columns are one message each. */
const LLMA_MESSAGE_SELECTOR = '.llma-msg-row, .llma-session-update, .llma-compare-col';

/**
 * Keyboard, focus and screen-reader support. It watches the page (message rows added, popovers and dialogs shown or hidden) instead of being
 * called from every place that does those things, so a new popover or dialog only has to be listed here, or only has to use .llma-overlay.
 */
class LLMAAccessibility {
    constructor() {
        this.ready = false;
        this.currentMessageId = null;
        this.messageCount = 0;
        this.announcer = null;
        this.pending = [];
        this.announceTimer = null;
        /** The last element focused outside every dialog: where focus goes back to when a dialog closes. */
        this.lastOutside = null;
        /** The popovers of the chat: the button that opens each one, and how to tell that it is open. A click on the button closes it again. */
        this.popovers = [
            { trigger: 'llma-params-btn', panel: 'llma-params-popover', label: 'Parameters', role: 'dialog', isOpen: panel => panel.style.display != 'none' },
            { trigger: 'llma-export-btn', panel: 'llma-export-menu', label: 'Export this chat', role: 'dialog', isOpen: panel => panel.style.display != 'none' },
            { trigger: 'llma-model-menu-btn', panel: 'llma-model-anchor', content: 'llma-model-select-wrap', label: 'Model selection', role: 'group', isOpen: panel => panel.classList.contains('llma-model-open') },
        ];
    }

    /** Wires everything. Safe to call more than once. */
    setup() {
        if (this.ready || !document.getElementById('llma-container')) {
            return;
        }
        this.ready = true;
        this.setupAnnouncer();
        this.setupMessages();
        this.setupPopovers();
        this.setupDialogs();
        for (let list of document.querySelectorAll('#llma-container .llma-modal-tabs')) {
            this.setupTabList(list, '.llma-modal-tab', tab => `llma-settings-${tab.dataset.tab}`);
        }
        this.setupTabList(document.getElementById('llma-instr-tabs'), '.llma-instr-tab', null);
        this.setupAttachButton();
        this.setupSplitButtons();
        for (let id of ['llma-settings-overlay', 'llma-params-popover']) {
            this.nameInputs(document.getElementById(id));
        }
        document.addEventListener('keydown', e => this.onEscape(e), true);
    }

    // ---- Live region ------------------------------------------------------------

    /** Adds the one polite live region everything is announced through. Replies are not a live region, so nothing is read token by token. */
    setupAnnouncer() {
        this.announcer = createDiv('llma-announcer', 'visually-hidden');
        this.announcer.setAttribute('role', 'status');
        this.announcer.setAttribute('aria-live', 'polite');
        this.announcer.setAttribute('aria-atomic', 'true');
        document.getElementById('llma-container').appendChild(this.announcer);
    }

    /** Says something to a screen reader. Messages that arrive together are read together. The text must already be translated. */
    announce(message) {
        if (!this.announcer || !message) {
            return;
        }
        if (!this.pending.includes(message)) {
            this.pending.push(message);
        }
        if (this.announceTimer) {
            return;
        }
        // Emptied first and filled a moment later, so the same words said twice are read twice.
        this.announcer.textContent = '';
        this.announceTimer = setTimeout(() => {
            this.announcer.textContent = this.pending.join('. ');
            this.pending = [];
            this.announceTimer = null;
        }, 100);
    }

    /** A reply has started (<paramref name="active"/> true) or ended, however it ended. */
    setBusy(active) {
        document.getElementById('llma-messages')?.setAttribute('aria-busy', active ? 'true' : 'false');
    }

    /** Announces that a reply failed, with the short form of why. */
    announceFailure(error) {
        let info = llmaHumanizeError(error);
        this.announce(`${translate('Reply failed')}: ${info.title}`);
    }

    // ---- Messages ---------------------------------------------------------------

    /** Makes the message list one Tab stop: arrows move between messages, Enter goes to a message's actions, Escape goes back. */
    setupMessages() {
        let container = document.getElementById('llma-messages');
        if (!container) {
            return;
        }
        // Only rows coming and going matter. A reply streaming inside a row changes nothing here.
        new MutationObserver(() => this.syncMessages()).observe(container, { childList: true });
        container.addEventListener('focusin', e => {
            let row = e.target.closest(LLMA_MESSAGE_SELECTOR);
            if (row && row.dataset.msgId != this.currentMessageId) {
                this.currentMessageId = row.dataset.msgId;
                this.syncMessages();
            }
        });
        container.addEventListener('keydown', e => this.onMessageKey(e));
        this.syncMessages();
    }

    /** The messages on screen, in reading order. */
    messageRows() {
        let container = document.getElementById('llma-messages');
        return container ? Array.from(container.querySelectorAll(LLMA_MESSAGE_SELECTOR)) : [];
    }

    /** Names each message and leaves exactly one of them (and its action bar) in the Tab order. */
    syncMessages() {
        let rows = this.messageRows();
        let container = document.getElementById('llma-messages');
        // A new message while the user is elsewhere (typing, say) makes the newest one the way back in.
        if (rows.length > this.messageCount && !container.contains(document.activeElement)) {
            this.currentMessageId = null;
        }
        this.messageCount = rows.length;
        let current = rows.find(r => r.dataset.msgId == this.currentMessageId) || rows[rows.length - 1];
        for (let row of rows) {
            row.setAttribute('role', 'article');
            row.setAttribute('aria-label', this.messageName(row));
            this.setCurrent(row, row == current);
        }
    }

    /** A message's accessible name. */
    messageName(row) {
        if (row.classList.contains('user')) {
            return translate('Your message');
        }
        if (row.classList.contains('llma-session-update')) {
            return translate('Report from a session');
        }
        if (row.classList.contains('llma-compare-col')) {
            let model = row.querySelector('.llma-compare-model')?.textContent || '';
            return `${translate('Reply')} ${Number(row.dataset.lane || 0) + 1}: ${model}`;
        }
        return `${translate('Reply from')} ${row.querySelector('.llma-msg-sender')?.textContent || translate('Assistant')}`;
    }

    /** Puts a message in, or takes it out of, the Tab order, along with its action bar and branch pager. */
    setCurrent(row, current) {
        row.tabIndex = current ? 0 : -1;
        for (let bar of row.querySelectorAll('[role="toolbar"]')) {
            let buttons = Array.from(bar.querySelectorAll('button'));
            let chosen = buttons.find(b => b.getAttribute('tabindex') == '0') || buttons[0];
            for (let button of buttons) {
                button.tabIndex = current && button == chosen ? 0 : -1;
            }
        }
        for (let button of row.querySelectorAll('.llma-branch-pill button')) {
            button.tabIndex = current ? 0 : -1;
        }
    }

    /** Moves keyboard focus to a message. */
    focusMessage(row) {
        if (!row) {
            return;
        }
        this.currentMessageId = row.dataset.msgId;
        this.syncMessages();
        row.focus({ preventScroll: true });
        row.scrollIntoView({ block: 'nearest' });
    }

    /** Focuses the message now at a position (after a delete), or the composer when no message is left. */
    focusMessageAt(index) {
        let rows = this.messageRows();
        if (rows.length == 0) {
            document.getElementById('llma-input')?.focus();
            return;
        }
        this.focusMessage(rows[Math.max(0, Math.min(index, rows.length - 1))]);
    }

    /** Arrow keys between messages (only while a message itself has focus) and along an action bar. */
    onMessageKey(e) {
        if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) {
            return;
        }
        let row = e.target.closest(LLMA_MESSAGE_SELECTOR);
        if (!row) {
            return;
        }
        if (e.target == row) {
            let rows = this.messageRows();
            let index = rows.indexOf(row);
            let next = { ArrowUp: index - 1, ArrowDown: index + 1, Home: 0, End: rows.length - 1 }[e.key];
            if (next !== undefined) {
                e.preventDefault();
                this.focusMessage(rows[Math.max(0, Math.min(next, rows.length - 1))]);
            }
            else if (e.key == 'Enter') {
                let first = row.querySelector('[role="toolbar"] button');
                if (first) {
                    e.preventDefault();
                    first.focus();
                }
            }
            else if (e.key == 'Escape') {
                e.preventDefault();
                document.getElementById('llma-input')?.focus();
            }
            return;
        }
        let bar = e.target.closest('[role="toolbar"]');
        if (!bar) {
            return;
        }
        if (e.key == 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            this.focusMessage(row);
            return;
        }
        let buttons = Array.from(bar.querySelectorAll('button')).filter(b => b.offsetParent !== null);
        let at = buttons.indexOf(e.target);
        let to = { ArrowLeft: at - 1, ArrowRight: at + 1, Home: 0, End: buttons.length - 1 }[e.key];
        if (to === undefined) {
            return;
        }
        e.preventDefault();
        let target = buttons[(to + buttons.length) % buttons.length];
        for (let button of bar.querySelectorAll('button')) {
            button.tabIndex = button == target ? 0 : -1;
        }
        target.focus();
    }

    // ---- Popovers ---------------------------------------------------------------

    /** Labels each popover, keeps aria-expanded on its button right, and moves focus into one opened from the keyboard. */
    setupPopovers() {
        for (let popover of this.popovers) {
            let trigger = document.getElementById(popover.trigger);
            let panel = document.getElementById(popover.panel);
            if (!trigger || !panel) {
                continue;
            }
            let content = document.getElementById(popover.content || popover.panel);
            content.setAttribute('role', popover.role);
            content.setAttribute('aria-label', translate(popover.label));
            trigger.setAttribute('aria-haspopup', popover.role == 'dialog' ? 'dialog' : 'true');
            trigger.setAttribute('aria-expanded', 'false');
            let byKeyboard = false;
            // A click made with Enter or Space reports no click count. Heard before the handler that opens the popover, which the observer below follows.
            trigger.addEventListener('click', e => byKeyboard = e.detail == 0, true);
            new MutationObserver(() => {
                let open = popover.isOpen(panel);
                if (trigger.getAttribute('aria-expanded') == String(open)) {
                    return;
                }
                trigger.setAttribute('aria-expanded', String(open));
                if (open && byKeyboard) {
                    this.focusableIn(content)[0]?.focus();
                }
                else if (!open && (content.contains(document.activeElement) && document.activeElement != trigger)) {
                    trigger.focus();
                }
            }).observe(panel, { attributes: true, attributeFilter: ['style', 'class'] });
        }
        let exportMenu = document.getElementById('llma-export-menu');
        exportMenu?.addEventListener('keydown', e => this.moveWithArrows(e, this.focusableIn(exportMenu)));
    }

    /** Up and Down move through a short list of buttons. */
    moveWithArrows(e, items) {
        if (e.key != 'ArrowUp' && e.key != 'ArrowDown') {
            return;
        }
        e.preventDefault();
        let at = items.indexOf(document.activeElement);
        let to = at + (e.key == 'ArrowDown' ? 1 : -1);
        items[(to + items.length) % items.length]?.focus();
    }

    /** The elements of a container the user can Tab to, that are on screen. */
    focusableIn(container) {
        return Array.from(container.querySelectorAll(LLMA_FOCUSABLE_SELECTOR)).filter(el => el.offsetParent !== null);
    }

    /** Escape closes the popover or drawer that is open and puts focus back on what opened it. */
    onEscape(e) {
        // A select2 list open inside the model popover closes first, by itself.
        if (e.key != 'Escape' || e.target.closest?.('.select2-container, .select2-dropdown') || !llmaIsTabActive()) {
            return;
        }
        let done = trigger => {
            e.preventDefault();
            e.stopPropagation();
            trigger?.focus();
        };
        for (let popover of this.popovers) {
            let trigger = document.getElementById(popover.trigger);
            let panel = document.getElementById(popover.panel);
            if (trigger && panel && popover.isOpen(panel) && panel.offsetParent !== null) {
                trigger.click();
                done(trigger);
                return;
            }
        }
        let caption = document.querySelector('#llma-container .llma-quick-caption-panel');
        if (caption) {
            let trigger = caption.parentElement.querySelector('button');
            caption.remove();
            trigger?.setAttribute('aria-expanded', 'false');
            done(trigger);
            return;
        }
        // The sidebar and the assistant panel slide over the chat on narrow screens.
        for (let [id, openClass, toggleId] of [['llma-sidebar', 'sidebar-open', 'llma-sidebar-toggle'], ['llma-asst-panel', 'panel-open', 'llma-asst-toggle']]) {
            let drawer = document.getElementById(id);
            if (drawer?.classList.contains(openClass) && !this.openDialog()) {
                drawer.classList.remove(openClass);
                let toggle = document.getElementById(toggleId);
                toggle?.classList.remove('active');
                done(toggle);
                return;
            }
        }
    }

    // ---- Dialogs ----------------------------------------------------------------

    /** The dialog showing now, or null. */
    openDialog() {
        return Array.from(document.querySelectorAll('#llma-container .llma-overlay')).find(o => o.style.display != 'none') || null;
    }

    /** For every dialog: Tab stays inside it, focus moves into it when it opens, and goes back where it was when it closes. */
    setupDialogs() {
        document.addEventListener('focusin', e => {
            if (e.target.closest && !e.target.closest('.llma-overlay')) {
                this.lastOutside = e.target;
            }
        });
        for (let overlay of document.querySelectorAll('#llma-container .llma-overlay')) {
            let wasOpen = overlay.style.display != 'none';
            let opener = null;
            new MutationObserver(() => {
                let open = overlay.style.display != 'none';
                if (open == wasOpen) {
                    return;
                }
                wasOpen = open;
                if (open) {
                    opener = this.lastOutside;
                    llmaInstallFocusTrap(overlay);
                    // After whatever the dialog's own code focuses.
                    setTimeout(() => {
                        if (overlay.style.display != 'none' && !overlay.contains(document.activeElement)) {
                            (overlay.querySelector('.llma-modal-close') || this.focusableIn(overlay)[0])?.focus();
                        }
                    }, LLMA_CONSTANTS.MODAL_FOCUS_DELAY_MS + 50);
                    return;
                }
                llmaRemoveFocusTrap(overlay);
                if (document.activeElement == document.body || overlay.contains(document.activeElement)) {
                    // The opener may be gone (a chat row redrawn) or hidden (a menu that closed): the composer is the fallback.
                    let back = opener?.isConnected && opener.offsetParent !== null ? opener : document.getElementById('llma-input');
                    back?.focus();
                }
            }).observe(overlay, { attributes: true, attributeFilter: ['style'] });
        }
    }

    // ---- Tab lists --------------------------------------------------------------

    /** Makes a row of tab buttons a tab list: one Tab stop, Left/Right/Home/End switch tabs. The tab with class "active" is the selected one. */
    setupTabList(list, selector, panelIdFor) {
        if (!list) {
            return;
        }
        list.setAttribute('role', 'tablist');
        let tabs = () => Array.from(list.querySelectorAll(selector)).filter(t => t.style.display != 'none');
        let sync = () => {
            for (let tab of list.querySelectorAll(selector)) {
                let selected = tab.classList.contains('active');
                tab.setAttribute('role', 'tab');
                tab.setAttribute('type', 'button');
                tab.setAttribute('aria-selected', String(selected));
                tab.tabIndex = selected ? 0 : -1;
                let panel = panelIdFor ? document.getElementById(panelIdFor(tab)) : null;
                if (panel) {
                    tab.id = tab.id || `${panel.id}-tab`;
                    tab.setAttribute('aria-controls', panel.id);
                    panel.setAttribute('role', 'tabpanel');
                    panel.setAttribute('aria-labelledby', tab.id);
                }
            }
        };
        new MutationObserver(sync).observe(list, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });
        list.addEventListener('keydown', e => {
            let all = tabs();
            let at = all.indexOf(e.target);
            let to = { ArrowLeft: at - 1, ArrowRight: at + 1, Home: 0, End: all.length - 1 }[e.key];
            if (at < 0 || to === undefined) {
                return;
            }
            e.preventDefault();
            let tab = all[(to + all.length) % all.length];
            tab.click();
            tab.focus();
        });
        sync();
    }

    // ---- Small pieces -----------------------------------------------------------

    /** The attach control is a label around a hidden file input, which the keyboard cannot reach: give it a button's behaviour. */
    setupAttachButton() {
        let attach = document.getElementById('llma-attach-label');
        if (!attach) {
            return;
        }
        attach.setAttribute('role', 'button');
        attach.tabIndex = 0;
        attach.addEventListener('keydown', e => {
            if (e.key == 'Enter' || e.key == ' ') {
                e.preventDefault();
                attach.click();
            }
        });
    }

    /** The arrows on the split bars open and close the side panels but are not buttons: they get a button's role and keys. */
    setupSplitButtons() {
        for (let arrow of document.querySelectorAll('#llma-container .llma-split-quickbutton')) {
            arrow.setAttribute('role', 'button');
            arrow.setAttribute('aria-label', arrow.title);
            arrow.tabIndex = 0;
            arrow.addEventListener('keydown', e => {
                if (e.key == 'Enter' || e.key == ' ') {
                    e.preventDefault();
                    arrow.click();
                }
            });
        }
    }

    /** Runs a redraw of a list and puts keyboard focus back on the same control of the same row, which the redraw replaced. */
    keepFocus(list, redraw) {
        let focused = list.contains(document.activeElement) ? document.activeElement : null;
        let rowId = focused?.closest('[data-id]')?.dataset.id;
        let kind = focused?.classList[0];
        redraw();
        if (!rowId || !kind) {
            return;
        }
        let same = Array.from(list.querySelectorAll(`.${CSS.escape(kind)}`)).find(el => el.closest('[data-id]')?.dataset.id == rowId);
        same?.focus({ preventScroll: true });
    }

    /** Core's form helpers write a field's name beside the field without tying the two together. Gives each such field that name, now and as forms are drawn. */
    nameInputs(root) {
        if (!root) {
            return;
        }
        let name = () => {
            for (let field of root.querySelectorAll('input:not([type="hidden"]):not([type="file"]), select, textarea')) {
                if (field.labels?.length || field.title || field.hasAttribute('aria-label') || field.hasAttribute('aria-labelledby')) {
                    continue;
                }
                let box = field.closest('.auto-input');
                let label = box?.querySelector('.auto-input-name')?.cloneNode(true);
                // The "?" that opens the field's help is not part of its name.
                label?.querySelector('.auto-input-qbutton')?.remove();
                let text = label?.textContent.trim() || field.getAttribute('placeholder');
                if (text) {
                    field.setAttribute('aria-label', text);
                }
            }
        };
        new MutationObserver(name).observe(root, { childList: true, subtree: true });
        name();
    }

    /** 'auto' for a user who asked for less motion, else 'smooth'. For scrollIntoView, which the stylesheet cannot reach. */
    scrollBehavior() {
        return window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth';
    }
}

let llmaA11y = new LLMAAccessibility();
