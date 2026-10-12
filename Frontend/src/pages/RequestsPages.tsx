import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Empty, ErrorBanner, Field, Loading, PageHeader, Pill, SuccessBanner } from '../components/ui'
import { attachmentUrl, errorMessage, get, postForm } from '../lib/api'
import { useAuth } from '../lib/auth'
import { dateTime } from '../lib/format'
import { caseUrl, Link } from '../lib/router'
import type { CaseRequest, RequestAttachment } from '../lib/types'

const MAX_FILES = 5
const MAX_BYTES = 4 * 1024 * 1024

export function AttachmentLinks({ requestId, attachments }: { requestId: number; attachments: RequestAttachment[] }) {
    if (attachments.length === 0) return <span className="muted small">No attachments</span>
    return (
        <ul className="attachment-list">
            {attachments.map((a) => (
                <li key={a.id}>
                    <a href={attachmentUrl(requestId, a.id)} target="_blank" rel="noreferrer">
                        📎 {a.fileName}
                    </a>{' '}
                    <span className="muted small">{Math.max(1, Math.round(a.sizeBytes / 1024))} KB</span>
                </li>
            ))}
        </ul>
    )
}

/** CSE: send a new account-opening request (details + documents) to Operations. */
export function SendRequestPage() {
    const { user } = useAuth()
    const [details, setDetails] = useState('')
    const [files, setFiles] = useState<File[]>([])
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState('')
    const [notice, setNotice] = useState('')
    const [mine, setMine] = useState<CaseRequest[]>([])
    const [loading, setLoading] = useState(true)
    const [inputKey, setInputKey] = useState(0)

    const load = useCallback(async () => {
        setLoading(true)
        try {
            setMine(await get<CaseRequest[]>('/requests'))
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [])

    useEffect(() => {
        void load()
    }, [load])

    function pick(list: FileList | null) {
        setError('')
        const chosen = Array.from(list ?? [])
        if (chosen.length > MAX_FILES) return setError(`Attach at most ${MAX_FILES} files.`)
        const big = chosen.find((f) => f.size > MAX_BYTES)
        if (big) return setError(`'${big.name}' is larger than 4 MB.`)
        setFiles(chosen)
    }

    async function submit(e: FormEvent) {
        e.preventDefault()
        setError('')
        setNotice('')
        setBusy(true)
        try {
            const form = new FormData()
            form.append('details', details)
            files.forEach((f) => form.append('files', f))
            const sent = await postForm<CaseRequest>('/requests', form)
            setNotice(`Request #${sent.id} sent to Operations with ${sent.attachments.length} attachment(s).`)
            setDetails('')
            setFiles([])
            setInputKey((k) => k + 1)
            await load()
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <>
            <PageHeader
                crumb="CSE / Send request"
                title="Send request to Operations"
                subtitle="Stage 1: send the client’s account-opening request and documents. Operations logs it in the tracker and will query you here if anything is missing."
            />
            {!user?.cseName && <ErrorBanner message="Your account is not linked to a CSE name, so you cannot send requests yet. Ask an administrator to set it." />}
            {notice && <SuccessBanner message={notice} />}
            {error && <ErrorBanner message={error} />}

            <form className="panel action-form narrow" onSubmit={submit}>
                <Field label="Request details *" help="Reference / request ID, client name, PAN, account type, physical or digital, location, segment, RM, and anything pending.">
                    <textarea
                        rows={6}
                        value={details}
                        onChange={(e) => setDetails(e.target.value)}
                        maxLength={4000}
                        required
                        placeholder="e.g. Ref MUM-2026-104, request 88231. Minor account for Rohan Mehta, PAN ABCPM1234K, physical form, Mumbai, Retail, RM Priya Nair. Guardian details to follow."
                    />
                </Field>
                <Field label="Documents (optional)" help="Up to 5 files, 4 MB each: JPG, PNG, WEBP or PDF. Use sample documents only in demos.">
                    <input key={inputKey} type="file" multiple accept=".jpg,.jpeg,.png,.webp,.pdf" onChange={(e) => pick(e.target.files)} />
                </Field>
                {files.length > 0 && (
                    <ul className="attachment-list">
                        {files.map((f) => (
                            <li key={f.name}>
                                📎 {f.name} <span className="muted small">{Math.max(1, Math.round(f.size / 1024))} KB</span>
                            </li>
                        ))}
                    </ul>
                )}
                <div className="form-actions">
                    <button type="submit" className="primary-button" disabled={busy || !user?.cseName || details.trim().length < 10}>
                        {busy ? 'Sending…' : 'Send to Operations'}
                    </button>
                </div>
            </form>

            <section className="panel register-panel">
                <div className="panel-heading register-heading">
                    <div>
                        <h2>My requests</h2>
                        <p>Once Operations logs a request, it becomes a case — open it to see queries and progress.</p>
                    </div>
                </div>
                {loading ? <Loading /> : <RequestTable rows={mine} showSender={false} />}
            </section>
        </>
    )
}

/** Operations: requests sent by CSEs, waiting to be logged in the tracker. */
export function IncomingRequestsPage() {
    const [rows, setRows] = useState<CaseRequest[]>([])
    const [status, setStatus] = useState<'Submitted' | 'Logged'>('Submitted')
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            setRows(await get<CaseRequest[]>(`/requests?status=${status}`))
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [status])

    useEffect(() => {
        void load()
    }, [load])

    return (
        <>
            <PageHeader
                crumb="Operations / Incoming requests"
                title="Incoming requests"
                subtitle="Requests sent by CSEs. Open one to log it in the tracker — AI fills the entry from the request for you to check."
                actions={
                    <button className="refresh-button" onClick={() => void load()}>
                        ↻ Refresh
                    </button>
                }
            />
            {error && <ErrorBanner message={error} onRetry={() => void load()} />}
            <section className="panel register-panel">
                <div className="register-tools">
                    <div className="segmented">
                        <button className={status === 'Submitted' ? 'active' : ''} onClick={() => setStatus('Submitted')}>
                            Waiting to be logged
                        </button>
                        <button className={status === 'Logged' ? 'active' : ''} onClick={() => setStatus('Logged')}>
                            Logged
                        </button>
                    </div>
                </div>
                {loading ? <Loading /> : <RequestTable rows={rows} showSender />}
            </section>
        </>
    )
}

function RequestTable({ rows, showSender }: { rows: CaseRequest[]; showSender: boolean }) {
    const { can, inWorkspace } = useAuth()
    const canLog = inWorkspace('Operations') && can('cases.create')
    if (rows.length === 0) return <Empty>No requests here.</Empty>
    return (
        <div className="table-scroll">
            <table>
                <thead>
                    <tr>
                        <th>Request</th>
                        {showSender && <th>From CSE</th>}
                        <th>Details</th>
                        <th>Documents</th>
                        <th>Status</th>
                        <th />
                    </tr>
                </thead>
                <tbody>
                    {rows.map((r) => (
                        <tr key={r.id}>
                            <td>
                                <strong>#{r.id}</strong>
                                <span className="cell-subtitle">{dateTime(r.submittedAt)}</span>
                            </td>
                            {showSender && (
                                <td>
                                    {r.cseName}
                                    <span className="cell-subtitle">{r.cseUserName}</span>
                                </td>
                            )}
                            <td className="wrap">{r.details.length > 180 ? r.details.slice(0, 180) + '…' : r.details}</td>
                            <td className="wrap">
                                <AttachmentLinks requestId={r.id} attachments={r.attachments} />
                            </td>
                            <td>
                                {r.status === 'Logged' ? (
                                    <>
                                        <Pill tone="success">Logged</Pill>
                                        {r.referenceId && (
                                            <span className="cell-subtitle">
                                                <Link to={caseUrl(r.referenceId)}>{r.referenceId}</Link>
                                            </span>
                                        )}
                                    </>
                                ) : (
                                    <Pill tone="warning">Waiting for Operations</Pill>
                                )}
                            </td>
                            <td>
                                {r.status === 'Submitted' && canLog && (
                                    <Link to={`/ops/new?request=${r.id}`} className="primary-button">
                                        Log in tracker
                                    </Link>
                                )}
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    )
}
