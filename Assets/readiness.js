/**
 * The setup check, at the foot of the General tab of the settings dialog: one button that asks the server whether the setup is ready (backends and models, tools, MCP servers, hooks,
 * skills, the knowledge library, schedules, sessions, storage) and shows each part with what is wrong and what to do. The check only reads; the "deep" one also asks the backends for their
 * models again and tries the knowledge library's embedding model, which are requests to other servers, so it is a separate button. Built from Bootstrap's alert, list-group and badge.
 */
class LLMAReadiness {

    constructor() {
        /** The badge colour for each status. */
        this.colors = { ok: 'success', warn: 'warning', fail: 'danger', info: 'secondary' };
        /** The words for each status. */
        this.words = { ok: 'OK', warn: 'Check', fail: 'Problem', info: 'Not set up' };
    }

    /** Draws the buttons (once). */
    setup() {
        let box = document.getElementById('llma-readiness');
        if (!box || box.dataset.built) {
            return;
        }
        box.dataset.built = 'true';
        box.innerHTML = `<h6>${translate('Setup check')}</h6>`
            + `<p class="small text-muted mb-2">${translate('Looks at the backends, models, tools, MCP servers, hooks, skills, knowledge library, schedules and storage, and says what to fix. It only reads. The deep check also asks the backends for their model lists again and tries the embedding model.')}</p>`
            + `<div class="d-flex flex-wrap gap-2 mb-2"><button type="button" class="basic-button" id="llma-readiness-run">${translate('Check setup')}</button>`
            + `<button type="button" class="basic-button" id="llma-readiness-deep">${translate('Deep check')}</button></div>`
            + '<div id="llma-readiness-out"></div>';
        document.getElementById('llma-readiness-run').addEventListener('click', () => this.run(false));
        document.getElementById('llma-readiness-deep').addEventListener('click', () => this.run(true));
    }

    /** Runs the check and shows it. */
    async run(deep) {
        let out = document.getElementById('llma-readiness-out');
        out.innerHTML = `<div class="small text-muted">${translate('Checking...')}</div>`;
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantReadiness', { deep: deep });
        }
        catch (err) {
            out.innerHTML = `<div class="alert alert-danger py-2">${escapeHtml(llmaShortError(err) || translate('The check could not run.'))}</div>`;
            return;
        }
        if (!reply || !reply.success) {
            out.innerHTML = `<div class="alert alert-danger py-2">${escapeHtml(reply?.error || translate('The check could not run.'))}</div>`;
            return;
        }
        this.render(out, reply);
    }

    /** Draws a report: the overall result, and each check with its fix. */
    render(out, report) {
        let summary = { ok: 'Everything looks ready.', warn: 'Ready, with something to look at.', fail: 'Something needs fixing.' };
        let html = `<div class="alert alert-${this.colors[report.overall] || 'secondary'} py-2" id="llma-readiness-overall" data-overall="${escapeHtml(report.overall)}">${translate(summary[report.overall] || report.overall)}${report.deep ? ` <small>(${translate('deep check')})</small>` : ''}</div>`
            + '<ul class="list-group">';
        for (let check of report.checks) {
            html += `<li class="list-group-item bg-transparent" data-check="${escapeHtml(check.id)}" data-status="${escapeHtml(check.status)}">`
                + `<span class="badge text-bg-${this.colors[check.status] || 'secondary'} me-2">${translate(this.words[check.status] || check.status)}</span><b>${escapeHtml(check.title)}</b>`
                + `<br><small class="text-break">${escapeHtml(check.detail)}</small>`
                + (check.fix ? `<br><small class="text-muted">${escapeHtml(translate('To fix'))}: ${escapeHtml(check.fix)}</small>` : '')
                + '</li>';
        }
        out.innerHTML = html + '</ul>';
    }
}

let llmaReadiness = new LLMAReadiness();

/** Wires the setup check. Called once when the settings dialog is set up. */
function llmaSetupReadiness() {
    llmaReadiness.setup();
}
