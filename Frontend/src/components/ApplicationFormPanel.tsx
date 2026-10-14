import { useCallback, useEffect, useRef, useState } from 'react'
import { documentUrl, errorMessage, get, post, put } from '../lib/api'
import { dateTime } from '../lib/format'
import type { ApplicationForm, AvailableAction, CaseDetail, CaseDocument, FormField } from '../lib/types'
import { useAiHealth } from './AiPanels'
import { Empty, ErrorBanner, Field, KeyValue, Pill } from './ui'

const PREPARING = 'Application Form Under Preparation'

type AiFillResult = { fields: Record<string, string | null>; warnings: string[]; notes: string | null }

// eslint-disable-next-line react-refresh/only-export-components
export function useApplicationForm(referenceId: string, enabled: boolean, version: string) {
    const [form, setForm] = useState<ApplicationForm | null>(null)
    const [error, setError] = useState('')
    const load = useCallback(async () => {
        if (!enabled) return
        try {
            setForm(await get<ApplicationForm>(`/cases/${encodeURIComponent(referenceId)}/form`))
            setError('')
        } catch (err) {
            setError(errorMessage(err))
        }
    }, [referenceId, enabled])
    useEffect(() => {
        void load()
    }, [load, version])
    return { form, setForm, error, reload: load }
}

function sections(fields: FormField[]) {
    const map = new Map<string, FormField[]>()
    for (const f of fields) map.set(f.section, [...(map.get(f.section) ?? []), f])
    return [...map.entries()]
}

/** Read-only form: case identity + every section. */
export function FormSummary({ form, compact = false }: { form: ApplicationForm; compact?: boolean }) {
    const c = form.case
    return (
        <div className={compact ? 'form-summary compact' : 'form-summary'}>
            <div>
                <h3>Client &amp; account (from the case)</h3>
                <KeyValue
                    items={[
                        ['Client name', c.clientName],
                        ['PAN', c.panNo],
                        ['Account type', c.accountType],
                        ['Channel', c.channel],
                        ['Segment', c.segment || '—'],
                        ['Location', c.location || '—'],
                        ['RM', c.relationshipManager || '—'],
                        ['CSE', c.cse || '—'],
                    ]}
                />
            </div>
            {sections(form.fields).map(([title, fields]) => (
                <div key={title}>
                    <h3>{title}</h3>
                    <KeyValue
                        items={fields.map((f) => [
                            f.label,
                            form.data[f.key] ? (
                                <span className={form.aiFilled.includes(f.key) ? 'ai-value' : undefined}>{form.data[f.key]}</span>
                            ) : f.required ? (
                                <span className="missing-value">Missing</span>
                            ) : (
                                '—'
                            ),
                        ])}
                    />
                </div>
            ))}
        </div>
    )
}

/**
 * Stage 2: the application form on the case page. Operations fills it (AI can
 * pre-fill from what the CSE sent), edits, saves, and sends it for signature.
 */
export function ApplicationFormPanel({ detail, onChanged }: { detail: CaseDetail; onChanged: (d: CaseDetail, message: string) => void }) {
    const a = detail.latest
    const visible = !a.isMasked && (a.latestStatus === PREPARING || detail.applicationFormStatus !== null)
    const { form, setForm, error, reload } = useApplicationForm(detail.referenceId, visible, `${a.latestStatus}|${detail.applicationFormStatus}`)
    const ai = useAiHealth()

    const [editing, setEditing] = useState(false)
    const [values, setValues] = useState<Record<string, string>>({})
    const [aiFilled, setAiFilled] = useState<string[]>([])
    const [aiWarnings, setAiWarnings] = useState<string[]>([])
    const [aiNotes, setAiNotes] = useState<string | null>(null)
    const [busy, setBusy] = useState('')
    const [localError, setLocalError] = useState('')
    const [dirty, setDirty] = useState(false)
    const autoRan = useRef(false)

    const sendAction = detail.availableActions.find((x) => x.code === 'send-form')

    const startEdit = useCallback((f: ApplicationForm) => {
        setValues({ ...f.data })
        setAiFilled(f.aiFilled)
        setDirty(false)
        setEditing(true)
    }, [])

    // Open straight in edit mode for a new or incomplete form Operations can edit.
    useEffect(() => {
        if (form && form.canEdit && (!form.saved || form.missing.length > 0) && !editing) startEdit(form)
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [form?.saved, form?.canEdit])

    const fillWithAi = useCallback(async () => {
        setBusy('ai')
        setLocalError('')
        try {
            const r = await post<AiFillResult>(`/cases/${encodeURIComponent(detail.referenceId)}/form/ai-fill`, {})
            // Fill empty fields only, so nothing Operations typed is overwritten.
            const added = Object.entries(r.fields).filter(([k, val]) => val && !values[k]).map(([k]) => k)
            setValues((v) => {
                const next = { ...v }
                for (const [k, val] of Object.entries(r.fields)) if (val && !next[k]) next[k] = val
                return next
            })
            setAiFilled((x) => [...new Set([...x, ...added])])
            setAiWarnings(
                added.length === 0
                    ? ['AI found no form details (date of birth, contact, address, bank, etc.) in the CSE’s request or query replies, so nothing was filled. Fill the form by hand, or ask the CSE for the details.', ...r.warnings]
                    : r.warnings,
            )
            setAiNotes(r.notes)
            if (added.length > 0) setDirty(true)
        } catch (err) {
            setLocalError(errorMessage(err))
        } finally {
            setBusy('')
        }
    }, [detail.referenceId, values])

    // Automatic mode: pre-fill once when the form is opened for the first time.
    useEffect(() => {
        if (autoRan.current || !form || !editing || form.saved || !form.canEdit) return
        if (detail.automationMode !== 'Automatic' || !ai.ready) return
        autoRan.current = true
        void fillWithAi()
    }, [form, editing, detail.automationMode, ai.ready, fillWithAi])

    if (!visible) return null

    async function save() {
        setBusy('save')
        setLocalError('')
        try {
            const saved = await put<ApplicationForm>(`/cases/${encodeURIComponent(detail.referenceId)}/form`, { data: values, aiFilled })
            setForm(saved)
            setDirty(false)
            if (saved.missing.length === 0) setEditing(false)
            // Refresh the case so the "Send form for signature" action appears once the form is complete.
            const updated = await get<CaseDetail>(`/cases/${encodeURIComponent(detail.referenceId)}`)
            onChanged(updated, saved.missing.length === 0 ? 'Application form saved and complete. Check it, then send it for signature.' : `Application form saved. Still missing: ${saved.missing.join(', ')}.`)
        } catch (err) {
            setLocalError(errorMessage(err))
        } finally {
            setBusy('')
        }
    }

    async function send() {
        setBusy('send')
        setLocalError('')
        try {
            const updated = await post<CaseDetail>(`/cases/${encodeURIComponent(detail.referenceId)}/actions/send-form`, {})
            onChanged(updated, `Form sent for signature. Status is now “${updated.latest.latestStatus}”.`)
            await reload()
        } catch (err) {
            setLocalError(errorMessage(err))
        } finally {
            setBusy('')
        }
    }

    const set = (k: string, v: string) => {
        setValues((x) => ({ ...x, [k]: v }))
        setAiFilled((x) => x.filter((key) => key !== k))
        setDirty(true)
    }

    const printUrl = `/cases/${encodeURIComponent(detail.referenceId)}/form/print`

    return (
        <section className="panel application-form-panel">
            <div className="panel-heading">
                <div>
                    <h2>Application form</h2>
                    <p>
                        {form?.status === 'Sent'
                            ? `Sent for signature ${form.sentAt ? dateTime(form.sentAt) : ''}. ${a.channel === 'Digital' ? 'Sent to the client.' : 'The CSE gets it signed by the client and sends it back here.'}`
                            : 'Stage 2: Operations prepares the form from the request, checks it, and sends it for signature.'}
                    </p>
                </div>
                {form && <Pill tone={form.status === 'Sent' ? 'success' : form.missing.length === 0 && form.saved ? 'info' : 'warning'}>{form.status === 'Sent' ? 'Sent' : !form.saved ? 'Not saved' : form.missing.length === 0 ? 'Ready to send' : 'Incomplete'}</Pill>}
            </div>

            {(error || localError) && <ErrorBanner message={localError || error} />}
            {!form ? (
                !error && <Empty>Loading form…</Empty>
            ) : editing ? (
                <>
                    {ai.allowed && form.canEdit && (
                        <div className="row-actions">
                            <button type="button" className="ai-button" onClick={() => void fillWithAi()} disabled={busy !== '' || !ai.ready}>
                                {busy === 'ai' ? 'Reading the CSE’s messages…' : '✦ Fill with AI'}
                            </button>
                            <span className="muted small">AI reads the CSE’s request and query replies and fills empty fields only. Check every highlighted value.</span>
                        </div>
                    )}
                    {aiWarnings.length > 0 && (
                        <ul className="ai-warning small">
                            {aiWarnings.map((w) => (
                                <li key={w}>{w}</li>
                            ))}
                        </ul>
                    )}
                    {aiNotes && <p className="small ai-note">✦ {aiNotes}</p>}

                    <div className="form-case-strip small">
                        <strong>{form.case.clientName}</strong> · PAN {form.case.panNo} · {form.case.accountType} · {form.case.channel} · {form.case.segment || 'No segment'} — from the case (change with “Edit details”).
                    </div>

                    {sections(form.fields).map(([title, fields]) => (
                        <fieldset key={title} className="form-section">
                            <legend>{title}</legend>
                            <div className="form-grid">
                                {fields.map((f) => (
                                    <Field key={f.key} label={`${f.label}${f.required ? ' *' : ''}`} help={f.help}>
                                        <FieldInput field={f} value={values[f.key] ?? ''} onChange={(v) => set(f.key, v)} highlight={aiFilled.includes(f.key)} />
                                    </Field>
                                ))}
                            </div>
                        </fieldset>
                    ))}

                    <div className="form-actions">
                        {form.saved && (
                            <button
                                type="button"
                                className="secondary-button"
                                onClick={() => {
                                    setEditing(false)
                                    setAiWarnings([])
                                    setAiNotes(null)
                                }}
                                disabled={busy !== ''}
                            >
                                Cancel
                            </button>
                        )}
                        <button type="button" className="primary-button" onClick={() => void save()} disabled={busy !== ''}>
                            {busy === 'save' ? 'Saving…' : 'Save form'}
                        </button>
                    </div>
                </>
            ) : (
                <>
                    <FormSummary form={form} />
                    {form.missing.length > 0 && <p className="ai-warning small">Missing: {form.missing.join(', ')}</p>}
                    <p className="muted small">
                        {form.updatedByName && `Last saved by ${form.updatedByName}${form.updatedAt ? `, ${dateTime(form.updatedAt)}` : ''}. `}
                        {form.aiFilled.length > 0 && <span className="ai-value">Highlighted</span>}
                        {form.aiFilled.length > 0 && ' values were filled by AI and confirmed on save.'}
                    </p>
                    <div className="form-actions">
                        <a className="secondary-button" href={printUrl} target="_blank" rel="noreferrer">
                            View / print form
                        </a>
                        {form.canEdit && (
                            <button type="button" className="secondary-button" onClick={() => startEdit(form)} disabled={busy !== ''}>
                                Edit
                            </button>
                        )}
                        {form.canEdit && (
                            <button
                                type="button"
                                className="primary-button"
                                onClick={() => void send()}
                                disabled={busy !== '' || dirty || !sendAction}
                                title={sendAction ? sendAction.description : detail.formNotReadyReason ?? undefined}
                            >
                                {busy === 'send' ? 'Sending…' : a.channel === 'Digital' ? 'Send to client for signature' : 'Send to CSE for signature'}
                            </button>
                        )}
                    </div>
                    {form.canEdit && !sendAction && detail.formNotReadyReason && <p className="muted small">{detail.formNotReadyReason}</p>}
                </>
            )}
        </section>
    )
}

function FieldInput({ field, value, onChange, highlight }: { field: FormField; value: string; onChange: (v: string) => void; highlight: boolean }) {
    const cls = highlight ? 'ai-filled' : undefined
    if (field.type === 'select')
        return (
            <select className={cls} value={value} onChange={(e) => onChange(e.target.value)}>
                <option value="">Select…</option>
                {(field.options ?? []).map((o) => (
                    <option key={o}>{o}</option>
                ))}
            </select>
        )
    if (field.type === 'multi') {
        const picked = value ? value.split(',').map((s) => s.trim()).filter(Boolean) : []
        const toggle = (o: string) => onChange((picked.includes(o) ? picked.filter((p) => p !== o) : [...picked, o]).join(', '))
        return (
            <div className={`checkbox-row ${cls ?? ''}`}>
                {(field.options ?? []).map((o) => (
                    <label key={o} className="checkbox">
                        <input type="checkbox" checked={picked.includes(o)} onChange={() => toggle(o)} /> {o}
                    </label>
                ))}
            </div>
        )
    }
    return (
        <input
            className={cls}
            type={field.type === 'date' ? 'date' : field.type === 'email' ? 'email' : field.type === 'tel' ? 'tel' : 'text'}
            value={value}
            onChange={(e) => onChange(field.key === 'ifsc' || field.key === 'guardianPan' ? e.target.value.toUpperCase() : e.target.value)}
            maxLength={200}
        />
    )
}

/** Documents uploaded on the case (signed form, supporting and corrected documents). */
export function DocumentList({ referenceId, documents }: { referenceId: string; documents: CaseDocument[] }) {
    if (documents.length === 0) return <Empty>No documents yet.</Empty>
    const groups = new Map<string, CaseDocument[]>()
    for (const d of documents) {
        const key = `${d.purpose} · ${d.uploadedByName} · ${dateTime(d.uploadedAt)}`
        groups.set(key, [...(groups.get(key) ?? []), d])
    }
    return (
        <div className="document-groups">
            {[...groups.entries()].map(([title, docs]) => (
                <div key={title} className="document-group">
                    <div className="small muted">{title}</div>
                    <div className="document-grid">
                        {docs.map((d) => (
                            <a key={d.id} className="document-tile" href={documentUrl(referenceId, d.id)} target="_blank" rel="noreferrer" title="Open full size">
                                {d.contentType.startsWith('image/') ? <img src={documentUrl(referenceId, d.id)} alt={d.fileName} loading="lazy" /> : <span className="document-icon">PDF</span>}
                                <span className="small">{d.fileName}</span>
                            </a>
                        ))}
                    </div>
                </div>
            ))}
        </div>
    )
}

const VERIFYING = ['Signed Form Received from CSE', 'Under Review — Signed Form', 'Discrepancy Resolution Received', 'Resubmitted Form Received — Under Review']

/**
 * Stage 3: Operations verifies the signed form — the form as prepared next to
 * the documents the CSE sent back, plus the AI pre-check. The decision is made
 * with the workflow actions (found in order / raise discrepancy / return form).
 */
export function SignedFormPanel({
    detail,
    onOpenAction,
    onChanged,
}: {
    detail: CaseDetail
    onOpenAction: (action: AvailableAction, initial: Record<string, string>, insightId?: number) => void
    onChanged: () => void
}) {
    const a = detail.latest
    const verifying = VERIFYING.includes(a.latestStatus)
    const show = !a.isMasked && (detail.documents.length > 0 || verifying)
    const { form } = useApplicationForm(detail.referenceId, show && detail.applicationFormStatus !== null, `${a.latestStatus}|${detail.documents.length}`)
    const ai = useAiHealth()
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState('')
    if (!show) return null

    const isOps = detail.availableActions.some((x) => x.team === 'Operations')
    const check = detail.aiInsights.find((i) => i.kind === 'DocumentCheck')
    const suggestion = check?.suggested && check.actionStatus === 'Pending' ? check.suggested : null
    const suggestedAction = suggestion
        ? detail.availableActions.find((x) => x.code === suggestion.action && (suggestion.queryId == null || x.queryId === suggestion.queryId))
        : undefined

    async function runCheck() {
        setBusy(true)
        setError('')
        try {
            await post(`/ai/cases/${encodeURIComponent(detail.referenceId)}/document-check`, {})
            onChanged()
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <section className="panel">
            <div className="panel-heading">
                <div>
                    <h2>Signed form &amp; documents</h2>
                    <p>
                        {verifying
                            ? 'Check the signed form and documents against the form that was sent, then record the outcome with the actions above (found in order, discrepancy, or return form).'
                            : 'Documents the CSE sent back on this case.'}
                    </p>
                </div>
                <span className="panel-tag">{detail.documents.length}</span>
            </div>

            {verifying && isOps && (
                <div className="ai-check-box" id="ai-precheck">
                    <div className="row-actions">
                        <strong>✦ AI pre-check</strong>
                        {check && <Pill tone={check.outcome === 'IssuesFound' ? 'warning' : check.outcome === 'NoIssuesFound' ? 'success' : 'neutral'}>{check.outcome === 'IssuesFound' ? 'Issues found' : check.outcome === 'NoIssuesFound' ? 'No issues found' : check.outcome === 'Failed' ? 'AI unavailable' : 'Needs a person'}</Pill>}
                        {ai.allowed && (
                            <button type="button" className="ai-button" onClick={() => void runCheck()} disabled={busy || !ai.ready}>
                                {busy ? 'Reading the documents…' : check ? 'Run again' : 'Run AI check'}
                            </button>
                        )}
                    </div>
                    {error && <div className="error-banner">{error}</div>}
                    {check ? (
                        <>
                            <p className="small">{check.summary}</p>
                            {check.findings.length > 0 && (
                                <ul className="small">
                                    {check.findings.map((f) => (
                                        <li key={f}>{f}</li>
                                    ))}
                                </ul>
                            )}
                            {suggestion && suggestedAction && (
                                <div className="row-actions">
                                    <button
                                        type="button"
                                        className="primary-button tone-warning"
                                        onClick={() =>
                                            onOpenAction(suggestedAction, {
                                                ...(suggestion.discrepancyType ? { discrepancyType: suggestion.discrepancyType } : {}),
                                                ...(suggestion.details ? { details: suggestion.details } : {}),
                                            }, check!.id)
                                        }
                                    >
                                        Review &amp; {suggestedAction.label.charAt(0).toLowerCase() + suggestedAction.label.slice(1)}
                                    </button>
                                    <span className="muted small">Opens the drafted message for you to edit. Nothing is sent until you confirm.</span>
                                </div>
                            )}
                        </>
                    ) : (
                        <p className="muted small">AI has not checked these documents yet. It reads up to 3 images or PDF pages and flags mismatches with the form, missing signatures and unreadable scans.</p>
                    )}
                </div>
            )}

            <div className={form && verifying ? 'verify-grid' : undefined}>
                {form && verifying && (
                    <div>
                        <h3 className="small">Form as sent for signature</h3>
                        <FormSummary form={form} compact />
                    </div>
                )}
                <div>
                    {form && verifying && <h3 className="small">Returned by the CSE</h3>}
                    <DocumentList referenceId={detail.referenceId} documents={detail.documents} />
                </div>
            </div>
        </section>
    )
}
