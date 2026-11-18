/**
 * The Schedules tab of the settings dialog: prompts that run later, on a timetable, as sessions (see the README, "Schedules"). The list shows when each runs next and how its
 * last run went, with a switch, Run now, Open (the chat its runs are listed under), Edit and Delete; the editor takes a title, the prompt and when it runs. A schedule the
 * assistant made is listed too and starts switched off: it does nothing until it is turned on here. Built from Bootstrap's list-group, badge and form-switch and SwarmUI's
 * make*Input helpers (as the other settings forms are), so it carries no styling of its own.
 */
class LLMASchedulesSettings {

    constructor() {
        /** The schedules as the server sent them. */
        this.items = [];
        /** The tools a schedule can be given: [{id, name}]. */
        this.toolChoices = [];
        /** The most schedules a user may have. */
        this.max = 20;
        /** The schedule being edited, or null for a new one (or when the editor is closed). */
        this.editing = null;
        /** Whether the editor is open. */
        this.editorOpen = false;
    }

    /** Loads the list from the server and draws it. */
    async load() {
        let list = document.getElementById('llma-schedules-list');
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetSchedules', {});
        }
        catch {
            reply = null;
        }
        if (!reply || !reply.success) {
            if (list) {
                list.innerHTML = `<div class="text-muted py-2">${translate('You do not have permission to use schedules.')}</div>`;
            }
            document.getElementById('llma-schedules-add').style.display = 'none';
            return;
        }
        this.items = reply.schedules || [];
        this.toolChoices = reply.toolChoices || [];
        this.max = reply.max || 20;
        this.renderList();
        let add = document.getElementById('llma-schedules-add');
        add.style.display = this.editorOpen ? 'none' : '';
        add.disabled = this.items.length >= this.max;
    }

    /** A time the way the user reads it, in their own zone. */
    when(iso) {
        let date = new Date(iso);
        return isNaN(date) ? '' : date.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
    }

    /** Draws the list. */
    renderList() {
        let list = document.getElementById('llma-schedules-list');
        if (!list) {
            return;
        }
        if (this.items.length == 0) {
            list.innerHTML = `<div class="text-muted py-2">${translate('No schedules yet. A schedule runs a prompt later, or again and again, in the background, and its runs appear as sessions under a chat of their own.')}</div>`;
            return;
        }
        let html = '<ul class="list-group mb-2">';
        for (let s of this.items) {
            let id = escapeHtml(s.id);
            let fromAssistant = s.source == 'assistant';
            let status = s.lastStatus ? `<span class="ms-1">${llmaSessions.dotHtml(s.lastStatus)} ${escapeHtml(llmaSessions.label(s.lastStatus))}</span>` : '';
            let next = s.enabled && s.nextRunAt ? `${translate('Next')}: ${escapeHtml(this.when(s.nextRunAt))}` : (s.enabled ? '' : translate('Off'));
            let last = s.lastRunAt ? `${translate('Last')}: ${escapeHtml(this.when(s.lastRunAt))}${status}` : translate('Never run');
            html += `<li class="list-group-item bg-transparent" data-schedule-id="${id}">`
                + `<div class="d-flex flex-wrap align-items-center gap-2">`
                + `<div class="form-check form-switch m-0"><input class="form-check-input llma-schedule-switch" type="checkbox" role="switch" ${s.enabled ? 'checked' : ''} aria-label="${escapeHtml(translate('On'))}"></div>`
                + `<span class="llma-schedule-text text-break"><b>${escapeHtml(s.title)}</b>`
                + (fromAssistant ? ` <span class="badge text-bg-warning" title="${escapeHtml(translate('The assistant made this. It does nothing until you turn it on.'))}">${translate('From the assistant')}</span>` : '')
                + ` <span class="badge text-bg-secondary">${escapeHtml(s.triggerText || '')}</span>`
                + `<br><small class="text-muted">${next}${next ? ' · ' : ''}${last}${s.runCount ? ` · ${s.runCount} ${translate('runs')}` : ''}</small>`
                + (s.lastSkipped ? `<br><small class="text-warning">${escapeHtml(translate('Skipped'))}: ${escapeHtml(s.lastSkipped)}</small>` : '')
                + `</span>`
                + `<span class="d-flex flex-wrap gap-2 ms-auto">`
                + `<button type="button" class="basic-button llma-schedule-run">${translate('Run now')}</button>`
                + (s.threadId ? `<button type="button" class="basic-button llma-schedule-open">${translate('Open')}</button>` : '')
                + `<button type="button" class="basic-button llma-schedule-edit">${translate('Edit')}</button>`
                + `<button type="button" class="basic-button llma-schedule-delete">${translate('Delete')}</button>`
                + `</span></div></li>`;
        }
        html += '</ul>';
        list.innerHTML = html;
        for (let row of list.querySelectorAll('li')) {
            let s = this.items.find(i => i.id == row.dataset.scheduleId);
            row.querySelector('.llma-schedule-switch').addEventListener('change', (e) => this.setEnabled(s, e.target.checked));
            row.querySelector('.llma-schedule-run').addEventListener('click', () => this.runNow(s));
            row.querySelector('.llma-schedule-open')?.addEventListener('click', () => this.open(s));
            row.querySelector('.llma-schedule-edit').addEventListener('click', () => this.openEditor(s));
            row.querySelector('.llma-schedule-delete').addEventListener('click', () => this.remove(s));
        }
    }

    /** Turns a schedule on or off, keeping everything else about it. */
    async setEnabled(schedule, enabled) {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSaveSchedule', { schedule: { ...schedule, enabled: enabled } }), translate('Could not change the schedule'));
        await this.load();
        return reply;
    }

    /** Starts a run now. */
    async runNow(schedule) {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantRunSchedule', { scheduleId: schedule.id }), translate('Could not start the run'));
        if (reply) {
            llmaShowToast(reply.started ? translate('Started a run.') : `${translate('Did not run')}: ${reply.reason}`, reply.started ? 'info' : 'error');
            await this.load();
            if (typeof llmaLoadThreads == 'function') {
                await llmaLoadThreads();
            }
        }
    }

    /** Closes the settings and opens the chat the schedule's runs are listed under. */
    open(schedule) {
        document.getElementById('llma-settings-close')?.click();
        llmaSwitchThread(schedule.threadId);
    }

    /** Deletes a schedule (its chat and runs stay). */
    async remove(schedule) {
        if (!confirm(`${translate('Delete')} "${schedule.title}"?`)) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantDeleteSchedule', { scheduleId: schedule.id }), translate('Could not delete'));
        if (reply) {
            if (this.editing && this.editing.id == schedule.id) {
                this.closeEditor();
            }
            await this.load();
        }
    }

    /** Which kind of "when" a stored trigger is in the editor: every, daily, weekly, cron or once. */
    kindOf(schedule) {
        let trigger = schedule.trigger || {};
        if (trigger.kind == 'every') {
            return 'every';
        }
        if (trigger.kind == 'at') {
            return 'once';
        }
        if (/^\d+ \d+ \* \* \*$/.test(trigger.cron || '')) {
            return 'daily';
        }
        return /^\d+ \d+ \* \* [0-6]$/.test(trigger.cron || '') ? 'weekly' : 'cron';
    }

    /** "09:30" from a daily or weekly cron expression. */
    timeOf(cron) {
        let parts = (cron || '').split(' ');
        return `${String(parts[1] ?? '9').padStart(2, '0')}:${String(parts[0] ?? '0').padStart(2, '0')}`;
    }

    /** A date and time as the text the "Date and time" box takes: YYYY-MM-DD HH:MM in the browser's own zone. */
    localInputValue(iso) {
        let date = new Date(iso);
        if (isNaN(date)) {
            return '';
        }
        let pad = n => String(n).padStart(2, '0');
        return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
    }

    /** Opens the editor on a schedule, or on a new one when none is given. */
    openEditor(schedule = null) {
        let zone = 'UTC';
        try {
            zone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
        }
        catch {
            zone = 'UTC';
        }
        let value = {
            title: '', prompt: '', trigger: { kind: 'every', everyMinutes: 60 }, timezone: zone, approvalMode: 'ask', model: '', enabled: true,
            toolIds: this.toolChoices.map(t => t.id),
        };
        if (schedule) {
            value = { ...value, ...schedule };
        }
        this.editing = schedule ? { ...schedule } : null;
        this.editorOpen = true;
        let kind = this.kindOf(value);
        let trigger = value.trigger || {};
        let weekday = kind == 'weekly' ? (trigger.cron || '').split(' ')[4] : '1';
        let models = LLMAState.availableModels || [];
        let forms = new LLMASettingsForms();
        let toolBoxes = this.toolChoices.map(t => forms.checkbox(`llma-sched-tool-${t.id}`, t.name || t.id, `Lets each run use ${t.name || t.id}.`, (value.toolIds || []).includes(t.id)));
        let html = forms.group('llma_sched_main', schedule ? translate('Edit schedule') : translate('New schedule'), translate('A schedule runs a prompt on a timetable, in the background, as a session. It runs with no one watching, so give it everything it needs in the prompt.'), [
            forms.text('llma-sched-title', 'Name', 'A short name, shown in the chat list.', value.title, 'normal'),
            forms.text('llma-sched-prompt', 'Prompt', 'What each run is asked to do. It cannot see any chat, so say everything it needs.', value.prompt, 'big'),
            forms.dropdown('llma-sched-kind', 'When', 'How often it runs.', ['every', 'daily', 'weekly', 'cron', 'once'], kind, ['Every so many minutes', 'Every day at a time', 'Every week on a day', 'A cron expression', 'Once']),
            forms.number('llma-sched-every', 'Every (minutes)', 'At least 5. For example 60 for every hour, 1440 for every day.', trigger.everyMinutes || 60, 5, 43200, 1),
            forms.text('llma-sched-time', 'At', 'The time of day, as HH:MM (24 hours), in the time zone below.', kind == 'daily' || kind == 'weekly' ? this.timeOf(trigger.cron) : '09:00', 'normal'),
            forms.dropdown('llma-sched-weekday', 'On', 'The day of the week.', ['1', '2', '3', '4', '5', '6', '0'], weekday, ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']),
            forms.text('llma-sched-cron', 'Cron expression', 'Five fields: minute hour day-of-month month day-of-week, for example 0 9 * * 1-5 for 9:00 on weekdays. Runs must be at least 5 minutes apart.', kind == 'cron' ? trigger.cron : '0 9 * * 1-5', 'normal'),
            forms.text('llma-sched-once', 'Date and time', 'When it runs, once, as YYYY-MM-DD HH:MM in this browser\'s time zone, for example 2026-12-01 09:00.', kind == 'once' ? this.localInputValue(trigger.at) : '', 'normal'),
            forms.text('llma-sched-zone', 'Time zone', 'The time zone the time and cron are read in, for example Europe/Paris.', value.timezone, 'normal'),
        ]) + forms.group('llma_sched_run', translate('How a run works'), translate('A run uses the model and tools below, as a session: it cannot use more than this, and a call that needs your approval waits for you to open the run.'), [
            forms.dropdown('llma-sched-approval', 'Approvals', 'Ask waits for you before anything that changes something (and is refused if you do not answer in time). Plan lets it only read. Auto lets it do everything without asking.', ['ask', 'plan', 'auto'], value.approvalMode, ['Ask before changes', 'Read only (plan)', 'Do everything (auto)']),
            forms.dropdown('llma-sched-model', 'Model', 'The model each run uses.', ['', ...models.map(m => m.id)], value.model || '', [translate('My default model'), ...models.map(m => m.name || m.id)]),
            forms.checkbox('llma-sched-enabled', 'On', 'A schedule that is off does not run.', value.enabled),
        ]) + (toolBoxes.length ? forms.group('llma_sched_tools', translate('Tools'), translate('The tools each run may use. A run never has the schedule or session tools.'), toolBoxes) : '')
            + `<div class="d-flex flex-wrap gap-2 mt-2 align-items-center"><button type="button" class="basic-button btn-primary" id="llma-sched-save">${translate('Save')}</button>`
            + `<button type="button" class="basic-button" id="llma-sched-cancel">${translate('Cancel')}</button>`
            + `<button type="button" class="basic-button" id="llma-sched-preview">${translate('Show the next runs')}</button></div>`
            + '<div id="llma-sched-preview-out" class="small text-muted mt-2"></div>';
        let editor = document.getElementById('llma-schedules-editor');
        editor.innerHTML = html;
        editor.style.display = '';
        document.getElementById('llma-sched-kind').addEventListener('change', () => this.updateVisibility());
        document.getElementById('llma-sched-save').addEventListener('click', () => this.save());
        document.getElementById('llma-sched-cancel').addEventListener('click', () => this.closeEditor());
        document.getElementById('llma-sched-preview').addEventListener('click', () => this.preview());
        document.getElementById('llma-schedules-add').style.display = 'none';
        this.updateVisibility();
        editor.scrollIntoView({ block: 'nearest' });
    }

    /** Shows the fields that apply to the chosen kind of "when". */
    updateVisibility() {
        let kind = document.getElementById('llma-sched-kind').value;
        let show = (id, visible) => {
            let box = findParentOfClass(document.getElementById(id), 'auto-input');
            if (box) {
                box.style.display = visible ? '' : 'none';
            }
        };
        show('llma-sched-every', kind == 'every');
        show('llma-sched-time', kind == 'daily' || kind == 'weekly');
        show('llma-sched-weekday', kind == 'weekly');
        show('llma-sched-cron', kind == 'cron');
        show('llma-sched-once', kind == 'once');
        show('llma-sched-zone', kind != 'every' && kind != 'once');
    }

    /** The trigger the editor describes, in the server's form, or null (with a toast) when it is not complete. */
    buildTrigger() {
        let get = id => document.getElementById(id).value.trim();
        let kind = get('llma-sched-kind');
        if (kind == 'every') {
            return { kind: 'every', everyMinutes: parseInt(get('llma-sched-every')) };
        }
        if (kind == 'cron') {
            return { kind: 'cron', cron: get('llma-sched-cron') };
        }
        if (kind == 'once') {
            let date = new Date(get('llma-sched-once').replace(' ', 'T'));
            if (isNaN(date)) {
                llmaShowToast(translate('Pick a date and time.'), 'error');
                return null;
            }
            return { kind: 'at', at: date.toISOString() };
        }
        let time = get('llma-sched-time').match(/^(\d{1,2}):(\d{2})$/);
        if (!time || parseInt(time[1]) > 23 || parseInt(time[2]) > 59) {
            llmaShowToast(translate('Write the time as HH:MM, for example 09:30.'), 'error');
            return null;
        }
        let day = kind == 'weekly' ? get('llma-sched-weekday') : '*';
        return { kind: 'cron', cron: `${parseInt(time[2])} ${parseInt(time[1])} * * ${day}` };
    }

    /** The zone the trigger is read in: the one in the box for cron, and UTC for "every" and "once", which are instants. */
    zoneFor(trigger) {
        return trigger.kind == 'cron' ? document.getElementById('llma-sched-zone').value.trim() : 'UTC';
    }

    /** Shows when the trigger in the editor would run next. */
    async preview() {
        let trigger = this.buildTrigger();
        let out = document.getElementById('llma-sched-preview-out');
        if (!trigger) {
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantPreviewSchedule', { trigger: trigger, timezone: this.zoneFor(trigger), count: 5 });
        }
        catch (err) {
            // The server says why a trigger is not allowed (too often, an unknown zone, ...).
            out.textContent = llmaShortError(err) || translate('That is not a valid schedule.');
            return;
        }
        out.textContent = reply.times.length ? `${translate('Next runs')}: ${reply.times.map(t => this.when(t.utc)).join(' · ')}` : translate('It never runs.');
    }

    /** Saves the schedule in the editor. */
    async save() {
        let get = id => document.getElementById(id);
        let trigger = this.buildTrigger();
        if (!trigger) {
            return;
        }
        let schedule = {
            title: get('llma-sched-title').value.trim(),
            prompt: get('llma-sched-prompt').value,
            trigger: trigger,
            timezone: this.zoneFor(trigger),
            approvalMode: get('llma-sched-approval').value,
            model: get('llma-sched-model').value,
            enabled: get('llma-sched-enabled').checked,
            toolIds: this.toolChoices.filter(t => get(`llma-sched-tool-${t.id}`)?.checked).map(t => t.id),
        };
        if (this.editing) {
            schedule.id = this.editing.id;
            schedule.assistantId = this.editing.assistantId || '';
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSaveSchedule', { schedule: schedule }), translate('Could not save'));
        if (!reply) {
            return;
        }
        this.closeEditor();
        await this.load();
    }

    /** Closes the editor. */
    closeEditor() {
        this.editing = null;
        this.editorOpen = false;
        let editor = document.getElementById('llma-schedules-editor');
        editor.style.display = 'none';
        editor.innerHTML = '';
        let add = document.getElementById('llma-schedules-add');
        add.style.display = '';
        add.disabled = this.items.length >= this.max;
    }
}

let llmaSchedulesSettings = new LLMASchedulesSettings();

/** Wires the Schedules tab's own button. Called once when the settings dialog is set up. */
function llmaSetupSchedulesTab() {
    let add = document.getElementById('llma-schedules-add');
    if (add) {
        add.addEventListener('click', () => llmaSchedulesSettings.openEditor(null));
    }
}
