import { useEffect, useState, type FormEvent } from 'react'
import { useAiHealth } from '../components/AiPanels'
import { ErrorBanner, Field, PageHeader } from '../components/ui'
import { errorMessage, get, post } from '../lib/api'
import { useAuth } from '../lib/auth'
import { dateTime, todayIso } from '../lib/format'
import { caseUrl, navigate, useLocation } from '../lib/router'
import type { Account, CaseRequest, Lookups } from '../lib/types'
import { AttachmentLinks } from './RequestsPages'

export function NewCasePage() {
    const { user } = useAuth()
    const [lookups, setLookups] = useState<Lookups | null>(null)
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)
    const [form, setForm] = useState({
        entryType: 'New',
        referenceId: '',
        requestId: '',
        clientName: '',
        panNo: '',
        accountType: 'Individual',
        channel: 'Physical',
        location: '',
        segment: '',
        cse: '',
        relationshipManager: '',
        processor: user?.fullName ?? '',
        inwardDate: todayIso(),
        remarks: '',
    })

    useEffect(() => {
        get<Lookups>('/lookups').then(setLookups).catch((err) => setError(errorMessage(err)))
    }, [])

    // Logging a CSE request from "Incoming requests" (?request=ID).
    const { search } = useLocation()
    const requestId = Number(search.get('request')) || null
    const [source, setSource] = useState<CaseRequest | null>(null)
    useEffect(() => {
        if (!requestId) return
        get<CaseRequest>(`/requests/${requestId}`)
            .then((r) => {
                setSource(r)
                setMessage(r.details)
                const received = new Date(r.submittedAt.endsWith('Z') ? r.submittedAt : `${r.submittedAt}Z`)
                const local = new Date(received.getTime() - received.getTimezoneOffset() * 60000).toISOString().slice(0, 10)
                setForm((f) => ({ ...f, cse: r.cseName, inwardDate: local }))
            })
            .catch((err) => setError(errorMessage(err)))
    }, [requestId])

    const set = (k: keyof typeof form, v: string) => setForm((f) => ({ ...f, [k]: v }))
    const ai = useAiHealth()
    const [message, setMessage] = useState('')
    const [extracting, setExtracting] = useState(false)
    const [aiFilled, setAiFilled] = useState<string[]>([])
    const [aiWarnings, setAiWarnings] = useState<string[]>([])

    async function extract() {
        setError('')
        setExtracting(true)
        try {
            type Extract = { fields: Record<string, string | null>; warnings: string[]; notes: string | null; confidence: number }
            const result = source
                ? await post<Extract>(`/requests/${source.id}/extract`, {})
                : await post<Extract>('/ai/extract-request', { text: message })
            // Work out the filled fields outside the state updater (React may run updaters twice in dev).
            const filled = Object.entries(result.fields)
                .filter(([key, value]) => value && key in form)
                .map(([key]) => key)
            if (result.notes) filled.push('remarks')
            setForm((f) => {
                const next = { ...f }
                for (const [key, value] of Object.entries(result.fields)) {
                    if (value && key in next) (next as Record<string, string>)[key] = value
                }
                if (result.notes) next.remarks = result.notes
                return next
            })
            setAiFilled(filled)
            setAiWarnings(result.warnings)
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setExtracting(false)
        }
    }

    const mark = (k: string) => (aiFilled.includes(k) ? 'ai-filled' : undefined)

    async function submit(e: FormEvent) {
        e.preventDefault()
        setError('')
        if (!/^[A-Z]{5}[0-9]{4}[A-Z]$/.test(form.panNo)) {
            setError('PAN must be in the format AAAAA9999A.')
            return
        }
        setBusy(true)
        try {
            const created = await post<Account>('/accounts', {
                ...form,
                referenceId: form.referenceId.trim(),
                sourceRequestId: source?.id ?? null,
                requestId: form.requestId.trim(),
                remarks: form.remarks || null,
                latestStatus: 'Request Received from CSE',
                actionBy: 'Operations',
            })
            navigate(caseUrl(created.referenceId))
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <>
            <PageHeader
                crumb="Operations / Log inward request"
                title="Log inward request"
                subtitle="Stage 1: record the request received from the CSE. It starts at “Request Received from CSE”; progress it from the case page."
            />
            {error && <ErrorBanner message={error} />}

            {source && (
                <section className="panel">
                    <div className="panel-heading">
                        <div>
                            <h2>CSE request #{source.id}</h2>
                            <p>
                                From {source.cseName} ({source.cseUserName}) · received {dateTime(source.submittedAt)}
                                {source.status !== 'Submitted' && ` · already logged as ${source.referenceId}`}
                            </p>
                        </div>
                    </div>
                    <p className="request-details">{source.details}</p>
                    <AttachmentLinks requestId={source.id} attachments={source.attachments} />
                </section>
            )}

            {ai.allowed && (
                <section className="panel ai-panel">
                    <div className="panel-heading">
                        <div>
                            <h2>{source ? '✦ Fill the entry from this request' : '✦ Quick fill (optional)'}</h2>
                            <p>
                                {source
                                    ? 'AI reads the CSE’s request and fills the form below for you to check. After saving, AI checks completeness and raises any query to the CSE automatically.'
                                    : 'Type or paste the request details received from the CSE. AI fills the form below for you to check. After saving, AI checks completeness and raises the query to CSE automatically.'}
                            </p>
                        </div>
                    </div>
                    {!source && <textarea
                        className="intake-text"
                        rows={6}
                        value={message}
                        onChange={(e) => setMessage(e.target.value)}
                        maxLength={6000}
                        placeholder={'e.g. Ref MUM-2026-104, request 88231. Minor account for Rohan Mehta, PAN ABCPM1234K, physical form, Mumbai, Retail, RM Priya Nair, CSE Amit Shah. Guardian details to follow.'}
                        aria-label="Request details"
                    />}
                    <div className="row-actions">
                        <button type="button" className="ai-button" onClick={() => void extract()} disabled={extracting || (!source && message.trim().length < 20) || !ai.ready || source?.status === 'Logged'}>
                            {extracting ? 'Reading…' : '✦ Fill form with AI'}
                        </button>
                        {!source && message.trim().length < 20 && (
                            <span className="muted small">Type or paste at least 20 characters of the CSE’s request ({message.trim().length}/20).</span>
                        )}
                        {!ai.ready && ai.health && <span className="muted small">{ai.health.message}</span>}
                    </div>
                    {aiFilled.length > 0 && <p className="small ai-note">✦ Filled {aiFilled.length} field(s), highlighted below. Please review before saving.</p>}
                    {aiWarnings.length > 0 && (
                        <ul className="ai-warning small">
                            {aiWarnings.map((w) => (
                                <li key={w}>{w}</li>
                            ))}
                        </ul>
                    )}
                </section>
            )}

            <form className="panel form-grid" onSubmit={submit}>
                <Field label="Entry Type" help="Resubmission / Discrepancy Resolution / Modification must reuse an existing Reference ID.">
                    <select value={form.entryType} onChange={(e) => set('entryType', e.target.value)}>
                        {(lookups?.entryTypes ?? ['New']).map((o) => (
                            <option key={o}>{o}</option>
                        ))}
                    </select>
                </Field>
                <Field label="Reference ID *" help="Unique per case; shared by all its records.">
                    <input className={mark('referenceId')} value={form.referenceId} onChange={(e) => set('referenceId', e.target.value)} required maxLength={100} />
                </Field>
                <Field label="Request ID *">
                    <input className={mark('requestId')} value={form.requestId} onChange={(e) => set('requestId', e.target.value)} required maxLength={100} />
                </Field>
                <Field label="Inward Date *">
                    <input type="date" className={mark('inwardDate')} value={form.inwardDate} max={todayIso()} onChange={(e) => set('inwardDate', e.target.value)} required />
                </Field>
                <Field label="Client Name *">
                    <input className={mark('clientName')} value={form.clientName} onChange={(e) => set('clientName', e.target.value)} required maxLength={200} />
                </Field>
                <Field label="PAN No *" help="Format AAAAA9999A. Use synthetic data for demos.">
                    <input className={mark('panNo')} value={form.panNo} onChange={(e) => set('panNo', e.target.value.toUpperCase())} required maxLength={10} />
                </Field>
                <Field label="Account Type">
                    <select className={mark('accountType')} value={form.accountType} onChange={(e) => set('accountType', e.target.value)}>
                        {(lookups?.accountTypes ?? ['Individual']).map((o) => (
                            <option key={o}>{o}</option>
                        ))}
                    </select>
                </Field>
                <Field label="Channel" help="Physical: form goes to CSE. Digital: form goes to the client.">
                    <select className={mark('channel')} value={form.channel} onChange={(e) => set('channel', e.target.value)}>
                        {(lookups?.channels ?? ['Physical', 'Digital']).map((o) => (
                            <option key={o}>{o}</option>
                        ))}
                    </select>
                </Field>
                <Field label="Location">
                    <input className={mark('location')} value={form.location} onChange={(e) => set('location', e.target.value)} list="nc-locations" maxLength={100} />
                </Field>
                <Field label="Segment">
                    <select className={mark('segment')} value={form.segment} onChange={(e) => set('segment', e.target.value)}>
                        <option value="">Select…</option>
                        {(lookups?.segmentOptions ?? []).map((o) => (
                            <option key={o}>{o}</option>
                        ))}
                    </select>
                </Field>
                <Field label="CSE Name" help="Must match the CSE user’s linked CSE name for them to see the case.">
                    <input className={mark('cse')} value={form.cse} onChange={(e) => set('cse', e.target.value)} list="nc-cses" maxLength={150} />
                </Field>
                <Field label="RM Name">
                    <input className={mark('relationshipManager')} value={form.relationshipManager} onChange={(e) => set('relationshipManager', e.target.value)} maxLength={150} />
                </Field>
                <Field label="Processor Name">
                    <input value={form.processor} onChange={(e) => set('processor', e.target.value)} maxLength={150} />
                </Field>
                <Field label="Remarks">
                    <textarea rows={2} className={mark('remarks')} value={form.remarks} onChange={(e) => set('remarks', e.target.value)} maxLength={2000} />
                </Field>
                <datalist id="nc-locations">{lookups?.locations.map((o) => <option key={o} value={o} />)}</datalist>
                <datalist id="nc-cses">{lookups?.cses.map((o) => <option key={o} value={o} />)}</datalist>
                <div className="form-actions span-all">
                    <button type="button" className="secondary-button" onClick={() => navigate('/ops')}>
                        Cancel
                    </button>
                    <button type="submit" className="primary-button" disabled={busy}>
                        {busy ? 'Saving…' : 'Log request'}
                    </button>
                </div>
            </form>
        </>
    )
}
