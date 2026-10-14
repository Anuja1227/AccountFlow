import { useState, type FormEvent } from 'react'
import { errorMessage, post, postForm } from '../lib/api'
import { date, todayIso } from '../lib/format'
import type { AvailableAction, CaseDetail, CaseQuery } from '../lib/types'
import { Field, Modal } from './ui'

type Values = Record<string, string>

const MAX_FILES = 5
const MAX_BYTES = 4 * 1024 * 1024

/** Form for one workflow action, submitted to the validated workflow endpoint. */
export function ActionForm({
    detail,
    action,
    query,
    initial,
    onClose,
    onDone,
}: {
    detail: CaseDetail
    action: AvailableAction
    query: CaseQuery | null
    initial?: Record<string, string>
    onClose: () => void
    onDone: (detail: CaseDetail, message: string) => void
}) {
    // Logging the account opening starts from the details MOFSL sent.
    const fromMofsl: Record<string, string> =
        action.code === 'record-account-opened'
            ? { accountNumber: detail.latest.mofslAccountNumber ?? '', actionDate: detail.latest.mofslAccountOpeningDate?.slice(0, 10) ?? todayIso() }
            : {}
    const [values, setValues] = useState<Values>(() =>
        Object.fromEntries(action.fields.map((f) => [f.name, initial?.[f.name] ?? fromMofsl[f.name] ?? (f.type === 'date' ? todayIso() : '')])),
    )
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)
    const [files, setFiles] = useState<File[]>([])

    function pickFiles(list: FileList | null) {
        setError('')
        const chosen = Array.from(list ?? [])
        if (chosen.length > MAX_FILES) return setError(`Attach at most ${MAX_FILES} files.`)
        const big = chosen.find((f) => f.size > MAX_BYTES)
        if (big) return setError(`'${big.name}' is larger than 4 MB.`)
        setFiles(chosen)
    }

    const set = (name: string, value: string) => setValues((v) => ({ ...v, [name]: value }))

    async function submit(event: FormEvent) {
        event.preventDefault()
        setError('')
        for (const f of action.fields) {
            if (f.type === 'files') {
                if (f.required && files.length === 0) {
                    setError(`${f.label} is required.`)
                    return
                }
                continue
            }
            if (f.required && !values[f.name]?.trim()) {
                setError(`${f.label} is required.`)
                return
            }
        }
        setBusy(true)
        try {
            const input = {
                queryId: action.queryId,
                details: values.details || null,
                discrepancyType: values.discrepancyType || null,
                mofslQueryType: values.mofslQueryType || null,
                accountNumber: values.accountNumber || null,
                reason: values.reason || null,
                actionDate: values.actionDate || null,
            }
            const url = `/cases/${encodeURIComponent(detail.referenceId)}/actions/${action.code}`
            let updated: CaseDetail
            if (files.length > 0) {
                const form = new FormData()
                form.append('input', JSON.stringify(input))
                files.forEach((f) => form.append('files', f))
                updated = await postForm<CaseDetail>(`${url}/upload`, form)
            } else {
                updated = await post<CaseDetail>(url, input)
            }
            const ai = action.code === 'cse-respond' && updated.aiInsights[0]?.kind === 'ResponseReview' ? ` AI review: ${updated.aiInsights[0].actionResult ?? updated.aiInsights[0].summary}` : ''
            onDone(updated, `${action.label}: done. Status is now “${updated.latest.latestStatus}”.${ai}`)
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <Modal title={action.label} onClose={onClose}>
            <p className="muted">{action.description}</p>
            {query && <QueryContext query={query} />}
            {action.code === 'record-account-opened' ? (
                <div className="query-context">
                    <div className="query-item-meta">Sent by MOFSL</div>
                    <p>
                        Account Number <strong>{detail.latest.mofslAccountNumber ?? '—'}</strong> · opened on <strong>{date(detail.latest.mofslAccountOpeningDate)}</strong>
                    </p>
                    {detail.latest.mofslAccountDetails && <p>{detail.latest.mofslAccountDetails}</p>}
                    <p className="muted small">The fields below are filled with these details. Check them, then confirm to log them in the tracker.</p>
                </div>
            ) : (
                initial && <p className="ai-note">✦ Pre-filled from the AI document check. Review and edit before you confirm.</p>
            )}
            {action.code === 'cse-respond' && detail.automationMode !== 'Off' && (
                <p className="ai-note">✦ After you send, AI checks whether your reply answers every point and either accepts it or sends a follow-up.</p>
            )}
            <form onSubmit={submit} className="action-form">
                {action.fields.map((f) => (
                    <Field key={f.name} label={`${f.label}${f.required ? ' *' : ''}`} help={f.help}>
                        {f.type === 'files' ? (
                            <>
                                <input type="file" multiple accept=".jpg,.jpeg,.png,.webp,.pdf" onChange={(e) => pickFiles(e.target.files)} />
                                {files.length > 0 && (
                                    <ul className="attachment-list">
                                        {files.map((file) => (
                                            <li key={file.name}>
                                                📎 {file.name} <span className="muted small">{Math.max(1, Math.round(file.size / 1024))} KB</span>
                                            </li>
                                        ))}
                                    </ul>
                                )}
                            </>
                        ) : f.type === 'textarea' ? (
                            <textarea rows={f.name === 'details' ? 6 : 3} value={values[f.name]} onChange={(e) => set(f.name, e.target.value)} maxLength={2000} required={f.required} />
                        ) : f.type === 'select' ? (
                            <select value={values[f.name]} onChange={(e) => set(f.name, e.target.value)} required={f.required}>
                                <option value="">Select…</option>
                                {(f.options ?? []).map((o) => (
                                    <option key={o}>{o}</option>
                                ))}
                            </select>
                        ) : (
                            <input
                                type={f.type === 'date' ? 'date' : 'text'}
                                value={values[f.name]}
                                max={f.type === 'date' ? todayIso() : undefined}
                                onChange={(e) => set(f.name, e.target.value)}
                                required={f.required}
                                maxLength={f.type === 'text' ? 100 : undefined}
                            />
                        )}
                    </Field>
                ))}
                {error && <div className="error-banner">{error}</div>}
                <div className="form-actions">
                    <button type="button" className="secondary-button" onClick={onClose}>
                        Cancel
                    </button>
                    <button type="submit" className={`primary-button tone-${action.tone}`} disabled={busy}>
                        {busy ? 'Saving…' : `Confirm: ${action.label}`}
                    </button>
                </div>
            </form>
        </Modal>
    )
}

function QueryContext({ query }: { query: CaseQuery }) {
    return (
        <div className="query-context">
            <div className="query-item-meta">
                Query #{query.id} · {query.queryTypeLabel}
                {query.category ? ` (${query.category})` : ''} · {query.fromTeam} → {query.toTeam} · {query.status}
            </div>
            {query.messages.slice(-3).map((m) => (
                <p key={m.id}>
                    <strong>
                        {m.authorName} · {m.messageType}:
                    </strong>{' '}
                    {m.body}
                </p>
            ))}
        </div>
    )
}
