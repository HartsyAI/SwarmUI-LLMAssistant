/**
 * Builds the settings forms from SwarmUI's own components instead of custom rows: the `make*Input` helpers in core's site.js
 * (which emit the same `auto-input` markup the Generate tab's parameters use), core's `input-group` sections, and core's "?" popovers
 * for explanations. Because the helpers take the element id, every field keeps the id the rest of the extension already reads.
 */
class LLMASettingsForms {

    /** Builds every form once. Safe to call again: a form that already has content is left alone. */
    build() {
        this.fill('llma-settings-general-form', [
            this.group('llma_general_generation', 'Generation', 'How replies are generated. A chat can override these from the sliders button in its top bar, and an assistant can override them too.', [
                this.slider('llma-s-temperature', 'Temperature', 'How random the replies are. Lower is more focused, higher is more varied.', 0.8, 0, 2, 0.05),
                this.number('llma-s-max-tokens', 'Max Tokens', 'The longest a reply may be, in tokens.', 4096, 1, 32768, 1),
                this.slider('llma-s-top-p', 'Top P', 'Nucleus sampling: only the most likely words that add up to this share of the probability are considered.', 0.9, 0, 1, 0.05),
                this.number('llma-s-seed', 'Seed', 'Makes replies repeatable on providers that support it. -1 means random.', -1, -1, 2147483647, 1),
                this.number('llma-s-context', 'Context Messages', 'How many earlier messages are sent with each request. 0 sends the whole chat.', 0, 0, 200, 1),
                this.number('llma-s-loops', 'Loops', 'Recurrent-block passes for a looped model such as Nemotron Dory, 1 to 4. 0 means not set. Other models ignore it.', 0, 0, 4, 1),
                this.checkbox('llma-s-stream', 'Stream', 'Show a reply as it is written instead of all at once.', true),
                this.slider('llma-s-image-max', 'Vision Image Max Size', 'Longest edge, in pixels, an attached image is shrunk to before it is sent. Lower means fewer vision tokens.', 1536, 256, 2048, 128)
            ]),
            this.group('llma_general_context', 'Context', 'How a long chat is kept within the model\'s memory. When it grows near the limit, the start of it is replaced by a short summary the model writes, so the chat can go on.', [
                this.number('llma-s-context-window', 'Context Window', 'The most tokens the model can hold, if you know better than the model\'s own listing. 0 uses the model\'s figure, and with none known, chats are never shortened on their own.', 0, 0, 2000000, 1024),
                this.checkbox('llma-s-auto-compact', 'Shorten Long Chats', 'Summarize the start of a chat on its own when it nears the limit. You can also type /compact in a chat.', true),
                this.slider('llma-s-compact-threshold', 'Shorten At', 'The share of the context window a chat may fill before it is shortened.', 0.8, 0.5, 0.95, 0.05)
            ]),
            this.group('llma_general_images', 'Image generation', 'What the assistant\'s generate_image tool uses when a chat does not say. Saved with Save & Close.', [
                this.text('llma-s-image-default-model', 'Default Image Model', 'The image model generate_image uses when no preset, model argument or params name one. Start typing to pick an installed model; leave empty for none (the assistant then lists your models and picks one).', ''),
                this.number('llma-s-image-batch-max', 'Most Images Per Call', 'The most images one generate_image call may make (1 to 16). A server limit: only an admin (llm_shared_write) can change it.', 4, 1, 16, 1)
            ]),
            this.group('llma_general_interface', 'Interface', 'Place LLM model files in Models/llm/. Configure an LLM backend under Server > Backends.', [
                this.checkbox('llma-s-markdown', 'Markdown Rendering', 'Render replies as formatted text.', true),
                this.checkbox('llma-s-enter-send', 'Enter to Send', 'Enter sends the message and Shift+Enter makes a new line. Off swaps them. Ctrl+Enter always sends.', true),
                this.checkbox('llma-s-show-tokens', 'Show Token Count', 'Show the running token count above the chat.', true)
            ])
        ]);
        this.fill('llma-settings-companion-form', [
            this.group('llma_companion_main', 'Floating Companion', 'A small in-page helper that floats over the SwarmUI UI. Ask it a quick question, send it your last image for a critique, or get prompt suggestions.', [
                this.checkbox('llma-s-companion-enabled', 'Enable Companion', 'Show the floating helper.', false),
                this.dropdown('llma-s-companion-persona', 'Persona', 'Which assistant speaks as the companion. By default it follows your active assistant.', [''], '', ['(Use my active assistant)']),
                this.dropdown('llma-s-companion-corner', 'Snap Corner', 'Which corner of the page the companion rests in.', ['bottom-right', 'bottom-left', 'top-right', 'top-left'], 'bottom-right', ['Bottom-right', 'Bottom-left', 'Top-right', 'Top-left']),
                this.slider('llma-s-companion-opacity', 'Opacity', 'How see-through the companion is.', 0.95, 0.4, 1, 0.05)
            ]),
            this.group('llma_companion_actions', 'Quick Actions', 'Which buttons the companion offers.', [
                this.checkbox('llma-s-companion-btn-ask', 'Ask anything', 'Ask the companion a free-form question.', true),
                this.checkbox('llma-s-companion-btn-critique', 'Critique my last image', 'Send your last generated image for feedback.', true),
                this.checkbox('llma-s-companion-btn-prompt', 'Help with my prompt', 'Get suggestions for the prompt you are writing.', true),
                this.checkbox('llma-s-companion-btn-preset', 'Suggest a preset', 'Suggest one of your saved Generate-tab presets that matches what you are trying to make.', true),
                this.checkbox('llma-s-companion-btn-explain', 'Explain a feature', 'Open an inline question; the companion looks up the answer in SwarmUI\'s bundled docs.', true),
                this.checkbox('llma-s-companion-btn-tip', 'Daily tip', 'Show one quick, useful SwarmUI tip.', true)
            ]),
            this.group('llma_companion_chatter', 'Ambient Chatter', 'Lets the companion speak up on its own. Each trigger has its own switch; Quiet Mode is a master mute that overrides all of them.', [
                this.checkbox('llma-s-companion-chat-quiet', 'Quiet Mode (master mute)', 'Keeps the overlay visible but suppresses every unsolicited message.', false),
                this.checkbox('llma-s-companion-chat-greeting', 'Greet on session start', 'Say hi once per browser session when you first load SwarmUI.', true),
                this.checkbox('llma-s-companion-chat-reactions', 'React to generations', 'One-line comment after a Generate-tab image finishes (throttled and capped).', false),
                this.checkbox('llma-s-companion-chat-idle', 'Idle nudges', 'Pipe up after you have been idle for the number of minutes below.', false),
                this.number('llma-s-companion-chat-idle-min', 'Idle threshold (minutes)', 'How long you must be idle before a nudge.', 8, 2, 120, 1),
                this.number('llma-s-companion-chat-cap', 'Max chatter per session', 'A hard cap on unsolicited messages per browser session, across all triggers.', 5, 0, 50, 1),
                this.checkbox('llma-s-companion-chat-qh', 'Use quiet hours', 'Suppress all chatter between the hours below (24-hour clock). Useful if you leave SwarmUI open overnight.', false),
                this.number('llma-s-companion-chat-qh-start', 'Quiet hours start', 'Hour the quiet period begins, 0 to 23.', 22, 0, 23, 1),
                this.number('llma-s-companion-chat-qh-end', 'Quiet hours end', 'Hour the quiet period ends, 0 to 23.', 8, 0, 23, 1)
            ])
        ]);
        this.fill('llma-params-form', [
            this.slider('llma-p-temperature', 'Temperature', 'How random the replies are. Lower is more focused, higher is more varied.', 0.8, 0, 2, 0.05),
            this.number('llma-p-max-tokens', 'Max Tokens', 'The longest a reply may be, in tokens.', 4096, 1, 32768, 1),
            this.slider('llma-p-top-p', 'Top P', 'Nucleus sampling: only the most likely words that add up to this share of the probability are considered.', 0.9, 0, 1, 0.05),
            this.number('llma-p-context', 'Context', 'How many earlier messages are sent with each request. 0 sends the whole chat.', 0, 0, 200, 1),
            this.number('llma-p-loops', 'Loops', 'Recurrent-block passes for a looped model such as Nemotron Dory. 0 means not set. Other models ignore it.', 0, 0, 4, 1)
        ]);
    }

    /** Puts the built html into a container and switches its sliders on. Does nothing if the container is missing or already built. */
    fill(containerId, parts) {
        let container = document.getElementById(containerId);
        if (!container || container.dataset.built) {
            return;
        }
        container.innerHTML = parts.join('');
        container.dataset.built = 'true';
        enableSlidersIn(container);
    }

    /** One of core's collapsible input groups, with a "?" popover on its header when it has a description. */
    group(id, title, description, inputs) {
        let popover = '';
        let button = '';
        if (description) {
            popover = `<div class="sui-popover sui-info-popover" id="popover_group_${id}"><b>${escapeHtml(title)}</b>:<br>&emsp;${escapeHtml(description)}</div>`;
            button = `<span class="auto-input-qbutton info-popover-button" onclick="doPopover('group_${id}', arguments[0])">?</span>`;
        }
        return `${popover}<div class="input-group input-group-open" id="auto-group-${id}"><span id="input_group_${id}" class="input-group-header input-group-shrinkable"><span class="header-label-wrap"><span class="auto-symbol">&#x2B9F;</span><span class="header-label">${translateableHtml(escapeHtml(title))}</span>${button}</span></span><div class="input-group-content" id="input_group_content_${id}" data-do_not_save="true">${inputs.join('')}</div></div>`;
    }

    /** A text box (core's makeTextInput, which adds its own popover). <paramref name="format"/> is 'normal' for one line or 'big' for several. */
    text(id, name, description, value, format = 'normal') {
        return makeTextInput(null, id, id, name, description, value, format, '', false, true);
    }

    /** A decimal or integer slider with its number box (core's makeSliderInput), plus its popover. */
    slider(id, name, description, value, min, max, step) {
        return makeGenericPopover(id, name, 'Number', description, '') + makeSliderInput(null, id, id, name, description, value, min, max, min, max, step);
    }

    /** A number box (core's makeNumberInput), plus its popover. */
    number(id, name, description, value, min, max, step) {
        return makeGenericPopover(id, name, 'Number', description, '') + makeNumberInput(null, id, id, name, description, value, min, max, step);
    }

    /** A checkbox (core's makeCheckboxInput, which adds its own popover). */
    checkbox(id, name, description, value) {
        return makeCheckboxInput(null, id, id, name, description, value, false, true);
    }

    /** A dropdown (core's makeDropdownInput), plus its popover. */
    dropdown(id, name, description, values, defaultValue, altNames) {
        return makeGenericPopover(id, name, 'Choice', description, '') + makeDropdownInput(null, id, id, name, description, values, defaultValue, false, true, altNames, true);
    }
}

let llmaSettingsForms = new LLMASettingsForms();
