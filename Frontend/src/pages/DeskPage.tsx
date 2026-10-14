import { useCallback, useEffect, useMemo, useState } from 'react'
import { CaseTable } from '../components/CaseTable'
import { Empty, ErrorBanner, Loading, PageHeader, Pill } from '../components/ui'
import { errorMessage, get } from '../lib/api'
import { P, useAuth, WS } from '../lib/auth'
import { date } from '../lib/format'
import { caseUrl, Link } from '../lib/router'
import type { Account, CaseQuery } from '../lib/types'

const CLOSED = ['Communication Sent — Case Closed', 'Rejected', 'Cancelled by Client']
const isOpen = (a: Account) => !a.accountOpeningDate && !CLOSED.includes(a.latestStatus)
const FORM_SENT = ['Physical Form Submitted to CSE', 'Digital Form Sent to Client']
const SIGNED_TO_VERIFY = ['Signed Form Received from CSE', 'Under Review — Signed Form']
const MOFSL_STAGE = ['Submitted to MOFSL', 'Query Resolved — Resubmitted to MOFSL']

type Workspace = 'Operations' | 'CSE' | 'MOFSL'

export function DeskPage({ workspace }: { workspace: Workspace }) {
    const { user, can } = useAuth()
    const [cases, setCases] = useState<Account[]>([])
    const [queries, setQueries] = useState<CaseQuery[]>([])
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')
    const [tab, setTab] = useState(0)

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            const [c, q] = await Promise.all([get<Account[]>('/accounts?view=cases'), get<CaseQuery[]>('/cases/queries?box=all')])
            setCases(c)
            setQueries(q)
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [])

    useEffect(() => {
        void load()
    }, [load])

    const tiles = useMemo(() => {
        const open = cases.filter(isOpen)
        const inbox = (status: string) => queries.filter((q) => q.toTeam === workspace && q.status === status)
        const outbox = (status: string) => queries.filter((q) => q.fromTeam === workspace && q.status === status)

        if (workspace === WS.operations) {
            return [
                { label: 'Awaiting Operations action', kind: 'cases' as const, cases: open.filter((a) => a.actionBy === WS.operations), hint: 'Cases where Operations owns the next step.' },
                { label: 'Signed forms to verify', kind: 'cases' as const, cases: open.filter((a) => SIGNED_TO_VERIFY.includes(a.latestStatus)), hint: 'The CSE sent the signed form back. Verify it against the form that was sent.' },
                { label: 'CSE responses to review', kind: 'queries' as const, queries: outbox('Responded'), hint: 'CSE has replied. Accept the response or follow up.' },
                { label: 'MOFSL queries to answer', kind: 'queries' as const, queries: inbox('Open'), hint: 'Queries raised by MOFSL on submitted cases.' },
                { label: 'Waiting on CSE', kind: 'queries' as const, queries: outbox('Open'), hint: 'Queries Operations raised that CSE has not answered yet.' },
                { label: 'With MOFSL', kind: 'cases' as const, cases: open.filter((a) => a.actionBy === WS.mofsl), hint: 'Submitted to MOFSL. Waiting for MOFSL to send the account details or raise a query.' },
                { label: 'Account details from MOFSL', kind: 'cases' as const, cases: open.filter((a) => MOFSL_STAGE.includes(a.latestStatus) && !!a.mofslAccountNumber), hint: 'MOFSL opened the account and sent the account number and opening date. Open the case and log them in the tracker.' },
                { label: 'SLA breached (open)', kind: 'cases' as const, cases: open.filter((a) => a.isSlaBreached), hint: 'Open cases beyond the SLA threshold.' },
            ]
        }
        if (workspace === WS.cse) {
            return [
                { label: 'Forms to get signed', kind: 'cases' as const, cases: open.filter((a) => FORM_SENT.includes(a.latestStatus)), hint: 'Operations sent the application form. Print it, get the client’s signature, then open the case and send the signed form back.' },
                { label: 'Queries to respond to', kind: 'queries' as const, queries: inbox('Open'), hint: 'Operations needs information or a correction from you.' },
                { label: 'Responses under review', kind: 'queries' as const, queries: inbox('Responded'), hint: 'You responded; Operations is reviewing.' },
                { label: 'My open cases', kind: 'cases' as const, cases: open, hint: 'Your cases that are still in progress.' },
                { label: 'History', kind: 'queries' as const, queries: inbox('Resolved'), hint: 'Resolved queries on your cases.' },
            ]
        }
        return [
            { label: 'Submissions to process', kind: 'cases' as const, cases: open.filter((a) => a.actionBy === WS.mofsl), hint: 'Submitted by Operations. Once the account is opened, send the account details to Operations, or raise a query if anything needs correcting.' },
            { label: 'Operations responses to review', kind: 'queries' as const, queries: outbox('Responded'), hint: 'Operations answered your query. Accept the response (then send the account details) or follow up.' },
            { label: 'Open MOFSL queries', kind: 'queries' as const, queries: outbox('Open'), hint: 'Waiting on Operations.' },
            { label: 'Accounts opened', kind: 'cases' as const, cases: cases.filter((a) => !!a.accountOpeningDate), hint: 'Cases with an account number and opening date.' },
        ]
    }, [cases, queries, workspace])

    const current = tiles[Math.min(tab, tiles.length - 1)]

    const titles: Record<Workspace, string> = {
        Operations: 'Operations desk',
        CSE: 'CSE desk',
        MOFSL: 'MOFSL desk',
    }

    const subtitle =
        workspace === WS.cse && !user?.cseName
            ? 'Your account is not linked to a CSE name yet, so no cases are assigned to you. Ask an administrator to set it.'
            : 'Your team’s queue. Open a case to take the next workflow action.'

    return (
        <>
            <PageHeader
                crumb={`Workspace / ${workspace}`}
                title={titles[workspace]}
                subtitle={subtitle}
                actions={
                    <>
                        {workspace === WS.operations && can(P.casesCreate) && (
                            <Link to="/ops/new" className="primary-button">
                                ＋ Log inward request
                            </Link>
                        )}
                        <button className="refresh-button" onClick={() => void load()}>
                            ↻ Refresh
                        </button>
                    </>
                }
            />

            {error && <ErrorBanner message={error} onRetry={() => void load()} />}

            <section className="queue-tiles">
                {tiles.map((t, i) => {
                    const count = t.kind === 'cases' ? t.cases!.length : t.queries!.length
                    return (
                        <button key={t.label} className={`queue-tile ${i === tab ? 'active' : ''}`} onClick={() => setTab(i)} aria-pressed={i === tab}>
                            <span className="queue-count">{loading ? '…' : count}</span>
                            <span className="queue-label">{t.label}</span>
                        </button>
                    )
                })}
            </section>

            <section className="panel register-panel">
                <div className="panel-heading register-heading">
                    <div>
                        <h2>{current.label}</h2>
                        <p>{current.hint}</p>
                    </div>
                </div>
                {loading ? (
                    <Loading />
                ) : current.kind === 'cases' ? (
                    <CaseTable rows={current.cases!} emptyText="Nothing here right now." />
                ) : (
                    <QueryList queries={current.queries!} />
                )}
            </section>
        </>
    )
}

export function QueryList({ queries }: { queries: CaseQuery[] }) {
    if (queries.length === 0) return <Empty>Nothing here right now.</Empty>
    return (
        <ul className="query-list">
            {queries.map((q) => {
                const last = q.messages[q.messages.length - 1]
                return (
                    <li key={q.id}>
                        <Link to={caseUrl(q.referenceId)} className="query-item">
                            <div className="query-item-head">
                                <strong>{q.referenceId}</strong>
                                <span className="muted small">{q.clientName}</span>
                                <Pill tone={q.status === 'Open' ? 'warning' : q.status === 'Responded' ? 'info' : 'success'}>{q.status}</Pill>
                            </div>
                            <div className="query-item-meta">
                                #{q.id} · {q.queryTypeLabel}
                                {q.category ? ` (${q.category})` : ''} · {q.fromTeam} → {q.toTeam} · raised {date(q.raisedAt)}
                                {q.followUpCount > 0 ? ` · ${q.followUpCount} follow-up(s)` : ''}
                            </div>
                            {last && (
                                <p className="query-item-body">
                                    <span className="muted">{last.authorTeam}:</span> {last.body.length > 220 ? last.body.slice(0, 220) + '…' : last.body}
                                </p>
                            )}
                        </Link>
                    </li>
                )
            })}
        </ul>
    )
}
