import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { ActionForm } from '../components/ActionForm'
import { AiInsightNote, AiSuggestionCard, CaseSummaryPanel, currentSuggestion, KIND_LABEL, OUTCOME_LABEL, OUTCOME_TONE, showConfidence, useAiHealth } from '../components/AiPanels'
import { ApplicationFormPanel, SignedFormPanel } from '../components/ApplicationFormPanel'
import { Empty, ErrorBanner, Field, KeyValue, Loading, Modal, PageHeader, Pill, StatusPill, SuccessBanner } from '../components/ui'
import { ApiError, errorMessage, get, post, put } from '../lib/api'
import { useAuth, WS } from '../lib/auth'
import { date, dateTime, number } from '../lib/format'
import { Link, navigate, useLocation } from '../lib/router'
import { AttachmentLinks } from './RequestsPages'
import type { Account, AiInsight, AvailableAction, CaseDetail, CaseQuery, Lookups, StageTat } from '../lib/types'

const STAGES = [
    { key: 'Stage 1', name: 'CSE request' },
    { key: 'Stage 2', name: 'Form preparation' },
    { key: 'Stage 3', name: 'Signed form' },
    { key: 'Stage 4', name: 'Resolution' },
    { key: 'Stage 5', name: 'MOFSL' },
    { key: 'Stage 6', name: 'Account opened' },
]

type TabKey = 'overview' | 'form' | 'documents' | 'queries' | 'records' | 'history'

const PREPARING = 'Application Form Under Preparation'
const FORM_STATUSES = [PREPARING, 'Physical Form Submitted to CSE', 'Digital Form Sent to Client']
const DOCUMENT_STATUSES = ['Signed Form Received from CSE', 'Under Review — Signed Form', 'Discrepancy Resolution Received', 'Resubmitted Form Received — Under Review']
const QUERY_STATUSES = ['Query Raised to CSE — Missing Information', 'Resubmitted by CSE', 'Discrepancy Raised to CSE', 'Form Returned to CSE', 'Query Raised by MOFSL', 'Query Resolved — Resubmitted to MOFSL']
const EXCEPTION_ACTIONS = ['put-on-hold', 'reject', 'cancel']

/** The tab that matters for the case's current status. */
function defaultTab(status: string, available: TabKey[]): TabKey {
    const pick: TabKey = FORM_STATUSES.includes(status) ? 'form' : DOCUMENT_STATUSES.includes(status) ? 'documents' : QUERY_STATUSES.includes(status) ? 'queries' : 'overview'
    return available.includes(pick) ? pick : 'overview'
}

export function CaseDetailPage({ referenceId }: { referenceId: string }) {
    const [detail, setDetail] = useState<CaseDetail | null>(null)
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')
    const [notFound, setNotFound] = useState(false)
    const [notice, setNotice] = useState('')
    const [active, setActive] = useState<AvailableAction | null>(null)
    const [activeInitial, setActiveInitial] = useState<Record<string, string> | undefined>(undefined)
    const [activeInsight, setActiveInsight] = useState<number | null>(null)
    const [editing, setEditing] = useState(false)
    const [version, setVersion] = useState(0)
    const { pathname, search } = useLocation()

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            setDetail(await get<CaseDetail>(`/cases/${encodeURIComponent(referenceId)}`))
        } catch (err) {
            if (err instanceof ApiError && err.status === 404) setNotFound(true)
            else setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [referenceId])

    useEffect(() => {
        void load()
    }, [load])

    if (loading && !detail) return <Loading label="Loading case…" />
    if (notFound)
        return (
            <>
                <PageHeader crumb="Cases" title="Case not found" />
                <Empty>
                    This case does not exist or you do not have access to it. <Link to="/register">Back to the register</Link>
                </Empty>
            </>
        )
    if (!detail) return error ? <ErrorBanner message={error} onRetry={() => void load()} /> : null

    const a = detail.latest
    const stageIndex = a.currentStage === 'Completed' ? 6 : STAGES.findIndex((s) => s.key === a.currentStage)
    const activeQuery = active?.queryId != null ? detail.queries.find((q) => q.id === active.queryId) ?? null : null

    // Tabs: hidden when they would be empty, or when client data is masked for this role.
    const showForm = !a.isMasked && (a.latestStatus === PREPARING || detail.applicationFormStatus !== null)
    const showDocs = !a.isMasked && (detail.attachments.length > 0 || detail.documents.length > 0 || DOCUMENT_STATUSES.includes(a.latestStatus))
    const openQueries = detail.queries.filter((q) => q.status !== 'Resolved').length
    const tabs: { key: TabKey; label: string }[] = [
        { key: 'overview', label: 'Overview' },
        ...(showForm ? [{ key: 'form' as TabKey, label: 'Application form' }] : []),
        ...(showDocs ? [{ key: 'documents' as TabKey, label: `Documents${detail.attachments.length + detail.documents.length ? ` · ${detail.attachments.length + detail.documents.length}` : ''}` }] : []),
        { key: 'queries', label: `Queries${detail.queries.length ? ` · ${detail.queries.length}` : ''}${openQueries ? ` (${openQueries} open)` : ''}` },
        { key: 'records', label: 'Records & TAT' },
        { key: 'history', label: 'History' },
    ]
    const keys = tabs.map((t) => t.key)
    const requested = search.get('tab') as TabKey | null
    const tab: TabKey = requested && keys.includes(requested) ? requested : defaultTab(a.latestStatus, keys)
    const setTab = (t: TabKey) => navigate(`${pathname}?tab=${t}`, { replace: true })

    const refresh = (message = '') => {
        setNotice(message)
        setVersion((v) => v + 1)
        void load()
    }
    const openAction = (act: AvailableAction, initial?: Record<string, string>, insightId?: number) => {
        setNotice('')
        setActiveInitial(initial)
        setActiveInsight(insightId ?? null)
        setActive(act)
    }

    const suggestion = currentSuggestion(detail)
    const mainActions = detail.availableActions.filter((x) => !EXCEPTION_ACTIONS.includes(x.code))
    const moreActions = detail.availableActions.filter((x) => EXCEPTION_ACTIONS.includes(x.code))
    const modeLabel = detail.automationMode === 'Automatic' ? 'Automatic' : detail.automationMode === 'SuggestOnly' ? 'Suggest only' : 'Off'

    return (
        <>
            <PageHeader
                crumb={
                    <>
                        <Link to="/register">Account register</Link> / {detail.referenceId}
                    </>
                }
                title={`${detail.referenceId} · ${a.clientName}`}
                subtitle={`${a.accountType} · ${a.channel} channel · ${a.segment || 'No segment'} · ${a.location || 'No location'} · CSE ${a.cse || '—'}`}
                actions={
                    <>
                        <span className="ai-mode-badge" title="AI automation mode (Admin → Settings)">
                            ✦ AI: {modeLabel}
                        </span>
                        {detail.canEditDetails && (
                            <button className="secondary-button" onClick={() => setEditing(true)}>
                                Edit details
                            </button>
                        )}
                        <button className="refresh-button" onClick={() => refresh()}>
                            ↻ Refresh
                        </button>
                    </>
                }
            />

            {notice && <SuccessBanner message={notice} />}
            {error && <ErrorBanner message={error} onRetry={() => void load()} />}
            {a.isMasked && <p className="muted small">Client identifiers are masked for your role.</p>}

            <ol className="stepper" aria-label="Workflow progress">
                {STAGES.map((s, i) => (
                    <li key={s.key} className={i < stageIndex ? 'done' : i === stageIndex ? 'current' : ''} aria-current={i === stageIndex ? 'step' : undefined}>
                        <span className="step-dot">{i < stageIndex ? '✓' : i + 1}</span>
                        <span className="step-name">{s.name}</span>
                    </li>
                ))}
            </ol>

            <section className="panel next-action-bar" aria-label="Next action">
                <div className="case-status-bar">
                    <StatusPill status={a.latestStatus} />
                    <span>
                        Next action by <strong>{a.actionBy}</strong>
                    </span>
                    <Pill tone={a.rftStatus === 'RFT' ? 'success' : 'warning'}>{a.rftStatus}</Pill>
                    <Pill tone={a.isSlaBreached ? 'danger' : 'success'}>{a.isSlaBreached ? 'SLA breached' : 'Within SLA'}</Pill>
                </div>
                {detail.availableActions.length === 0 ? (
                    <p className="muted small">No actions available to you right now. {a.actionBy !== '' && `Waiting on ${a.actionBy}.`}</p>
                ) : (
                    <div className="action-buttons">
                        {mainActions.map((act) => (
                            <button key={`${act.code}-${act.queryId ?? ''}`} className={`action-button tone-${act.tone}`} onClick={() => openAction(act)} title={act.description}>
                                {act.label}
                                {act.queryId ? ` · #${act.queryId}` : ''}
                            </button>
                        ))}
                        {moreActions.length > 0 && (
                            <details className="more-menu">
                                <summary className="action-button tone-neutral">More ▾</summary>
                                <div className="more-menu-list">
                                    {moreActions.map((act) => (
                                        <button key={act.code} type="button" onClick={() => openAction(act)} title={act.description}>
                                            {act.label}
                                        </button>
                                    ))}
                                </div>
                            </details>
                        )}
                    </div>
                )}
                {suggestion && (
                    <AiSuggestionCard
                        detail={detail}
                        insight={suggestion}
                        onReview={(act, initial, id) => openAction(act, initial, id)}
                        onChanged={(message) => refresh(message)}
                    />
                )}
            </section>

            <div className="case-tabs" role="tablist" aria-label="Case sections">
                {tabs.map((t) => (
                    <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} className={tab === t.key ? 'active' : ''} onClick={() => setTab(t.key)}>
                        {t.label}
                    </button>
                ))}
            </div>

            <div role="tabpanel" className="case-tab-panel">
                {tab === 'overview' && <OverviewTab detail={detail} version={version} onOpenTab={setTab} onChanged={refresh} />}
                {tab === 'form' && <ApplicationFormPanel detail={detail} onChanged={(updated, message) => { setDetail(updated); setNotice(message); setVersion((v) => v + 1) }} />}
                {tab === 'documents' && (
                    <>
                        <SignedFormPanel detail={detail} onOpenAction={openAction} onChanged={() => refresh()} />
                        {detail.attachments.length > 0 && (
                            <section className="panel">
                                <div className="panel-heading">
                                    <div>
                                        <h2>Documents from the CSE request</h2>
                                        <p>Attached by the CSE when the request was sent (request #{detail.attachments[0].requestId}).</p>
                                    </div>
                                    <span className="panel-tag">{detail.attachments.length}</span>
                                </div>
                                {[...new Set(detail.attachments.map((x) => x.requestId))].map((rid) => (
                                    <AttachmentLinks key={rid} requestId={rid} attachments={detail.attachments.filter((x) => x.requestId === rid)} />
                                ))}
                            </section>
                        )}
                    </>
                )}
                {tab === 'queries' && <QueriesTab detail={detail} onReview={openAction} onChanged={refresh} />}
                {tab === 'records' && <RecordsTab detail={detail} />}
                {tab === 'history' && <HistoryTab detail={detail} />}
            </div>

            {active && (
                <ActionForm
                    detail={detail}
                    action={active}
                    query={activeQuery}
                    initial={activeInitial}
                    onClose={() => setActive(null)}
                    onDone={(updated, message) => {
                        if (activeInsight != null) void post(`/ai/insights/${activeInsight}/used`).catch(() => undefined)
                        setDetail(updated)
                        setActive(null)
                        setActiveInsight(null)
                        setNotice(message)
                        setVersion((v) => v + 1)
                        if (activeInsight != null) void load()
                    }}
                />
            )}

            {editing && (
                <EditDetails
                    detail={detail}
                    onClose={() => setEditing(false)}
                    onSaved={(updated) => {
                        setDetail(updated)
                        setEditing(false)
                        setNotice('Case details saved.')
                        setVersion((v) => v + 1)
                    }}
                />
            )}
        </>
    )
}

// ------------------------------------------------------------------ Tabs

function OverviewTab({ detail, version, onOpenTab, onChanged }: { detail: CaseDetail; version: number; onOpenTab: (t: TabKey) => void; onChanged: (message?: string) => void }) {
    const a = detail.latest
    const last = detail.history[0]
    const open = detail.queries.filter((q) => q.status !== 'Resolved')
    const intake = detail.aiInsights.find((i) => i.kind === 'IntakeCheck')
    const { inWorkspace, can } = useAuth()
    const ai = useAiHealth()
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState('')
    const intakeStage = ['Request Received from CSE', 'Under Review by Operations'].includes(a.latestStatus)
    const canRunIntake = intakeStage && inWorkspace(WS.operations) && can('workflow.operations') && ai.allowed

    async function runIntake() {
        setBusy(true)
        setError('')
        try {
            await post(`/ai/cases/${encodeURIComponent(detail.referenceId)}/intake-check`)
            onChanged('AI intake check finished.')
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <div className="overview-grid">
            <div className="overview-main">
                <section className="panel">
                    <div className="panel-heading">
                        <div>
                            <h2>Where this case is</h2>
                        </div>
                    </div>
                    <KeyValue
                        items={[
                            ['Current Stage', `${a.currentStage}${a.isCurrentStageManuallyOverridden ? ' (set manually)' : ''}`],
                            ['Latest Status', a.latestStatus],
                            ['Action By / Current Owner', a.actionBy || '—'],
                            ['Last update', last ? `${last.action} · ${last.userName} · ${dateTime(last.createdAt)}` : '—'],
                        ]}
                    />
                    {open.length > 0 && (
                        <div className="open-queries">
                            {open.map((q) => {
                                const m = q.messages[q.messages.length - 1]
                                return (
                                    <div key={q.id} className="open-query">
                                        <strong>
                                            #{q.id} {q.queryTypeLabel}
                                            {q.category ? ` · ${q.category}` : ''}
                                        </strong>{' '}
                                        <Pill tone={q.status === 'Open' ? 'warning' : 'info'}>{q.status}</Pill>
                                        {m && (
                                            <p className="small">
                                                <span className="muted">{m.authorTeam}:</span> {m.body.length > 200 ? m.body.slice(0, 200) + '…' : m.body}
                                            </p>
                                        )}
                                        <button type="button" className="link-button" onClick={() => onOpenTab('queries')}>
                                            Open in Queries →
                                        </button>
                                    </div>
                                )
                            })}
                        </div>
                    )}
                    {(intake || canRunIntake) && (
                        <div className="overview-ai">
                            {intake && <AiInsightNote insight={intake} />}
                            {canRunIntake && (
                                <button type="button" className="ai-button" disabled={busy || !ai.ready} onClick={() => void runIntake()}>
                                    {busy ? 'Checking…' : intake ? 'Run intake check again' : '✦ Run AI intake check'}
                                </button>
                            )}
                            {error && <div className="error-banner">{error}</div>}
                        </div>
                    )}
                </section>

                <section className="panel">
                    <div className="panel-heading">
                        <div>
                            <h2>Key details</h2>
                        </div>
                    </div>
                    <KeyValue
                        items={[
                            ['Reference ID', a.referenceId],
                            ['Request ID', a.requestId],
                            ['Entry Type', a.entryType],
                            ['Client Name', a.clientName],
                            ['PAN No', a.panNo],
                            ['Account Type', a.accountType],
                            ['Channel', a.channel],
                            ['Location', a.location || '—'],
                            ['Segment', a.segment || '—'],
                            ['RM Name', a.relationshipManager || '—'],
                            ['CSE Name', a.cse || '—'],
                            ['Processor Name', a.processor || '—'],
                        ]}
                    />
                </section>
            </div>

            <aside className="overview-side">
                <section className="panel">
                    <div className="panel-heading">
                        <div>
                            <h2>Turnaround</h2>
                            <p>Working days, Mon–Fri</p>
                        </div>
                    </div>
                    <KeyValue
                        items={[
                            ['Gross TAT', `${number(a.grossTatDays)} days`],
                            ['Net TAT', `${number(a.netTatDays)} days`],
                            ['Current Aging', a.accountOpeningDate ? 'Closed' : `${number(a.currentAgingDays)} days`],
                            ['SLA Breach', `${a.isSlaBreached ? 'Yes' : 'No'} (threshold ${detail.slaThresholdWorkingDays} days)`],
                        ]}
                    />
                    <StageTatTable stageTats={a.stageTats ?? []} />
                    <button type="button" className="link-button" onClick={() => onOpenTab('records')}>
                        Full TAT &amp; records →
                    </button>
                </section>
                <CaseSummaryPanel referenceId={detail.referenceId} version={version} />
            </aside>
        </div>
    )
}

/** Stage 1–6 TAT and stage-wise SLA (working days spent in each stage). */
function StageTatTable({ stageTats }: { stageTats: StageTat[] }) {
    if (stageTats.length === 0) return null
    return (
        <table className="stage-tat-table" aria-label="Time in each stage">
            <thead>
                <tr>
                    <th>Stage 1–6 TAT</th>
                    <th>Since</th>
                    <th className="num">Days</th>
                </tr>
            </thead>
            <tbody>
                {stageTats.map((t) => (
                    <tr key={t.stage} className={t.days == null ? 'muted' : undefined}>
                        <td>{t.stage}</td>
                        <td>{t.startDate ? date(t.startDate) : t.stage === 'Stage 4' && stageTats.some((x) => x.stage === 'Stage 5' && x.days != null) ? 'Not needed' : '—'}</td>
                        <td className="num">
                            {t.days == null ? '—' : `${number(t.days)} d${t.inProgress ? ' so far' : ''}`}{' '}
                            {t.isSlaBreached && <Pill tone="danger">Over SLA</Pill>}
                        </td>
                    </tr>
                ))}
            </tbody>
        </table>
    )
}

function QueriesTab({
    detail,
    onReview,
    onChanged,
}: {
    detail: CaseDetail
    onReview: (action: AvailableAction, initial?: Record<string, string>, insightId?: number) => void
    onChanged: (message?: string) => void
}) {
    const [filter, setFilter] = useState<'open' | 'resolved' | 'all'>('all')
    const [expanded, setExpanded] = useState<Record<number, boolean>>({})
    const [busy, setBusy] = useState<number | null>(null)
    const [error, setError] = useState('')
    const { inWorkspace, can } = useAuth()
    const ai = useAiHealth()
    const isOps = inWorkspace(WS.operations) && can('workflow.operations')

    if (detail.queries.length === 0)
        return (
            <section className="panel">
                <Empty>No queries on this case — it is on track for Right First Time.</Empty>
            </section>
        )

    const rows = detail.queries
        .slice()
        .sort((x, y) => Number(x.status === 'Resolved') - Number(y.status === 'Resolved') || y.id - x.id)
        .filter((q) => filter === 'all' || (filter === 'open' ? q.status !== 'Resolved' : q.status === 'Resolved'))

    async function review(q: CaseQuery) {
        setBusy(q.id)
        setError('')
        try {
            await post(`/ai/cases/${encodeURIComponent(detail.referenceId)}/queries/${q.id}/review`)
            onChanged('AI review finished.')
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(null)
        }
    }

    return (
        <section className="panel">
            <div className="register-tools">
                <div className="segmented">
                    {(['all', 'open', 'resolved'] as const).map((f) => (
                        <button key={f} className={filter === f ? 'active' : ''} onClick={() => setFilter(f)}>
                            {f === 'all' ? 'All' : f === 'open' ? 'Open' : 'Resolved'}
                        </button>
                    ))}
                </div>
            </div>
            {error && <div className="error-banner">{error}</div>}
            <div className="threads">
                {rows.map((q) => {
                    const resolved = q.status === 'Resolved'
                    const show = expanded[q.id] ?? !resolved
                    const reviewInsight = detail.aiInsights.find((i) => i.kind === 'ResponseReview' && i.queryId === q.id)
                    const target = reviewInsight?.suggested && reviewInsight.actionStatus === 'Pending'
                        ? detail.availableActions.find((x) => x.code === reviewInsight.suggested!.action && x.queryId === q.id)
                        : undefined
                    return (
                        <article key={q.id} className="thread">
                            <header>
                                <strong>
                                    #{q.id} {q.queryTypeLabel}
                                    {q.category ? ` · ${q.category}` : ''}
                                </strong>
                                <span className="muted small">
                                    {q.stage} · {q.fromTeam} → {q.toTeam} · raised {date(q.raisedAt)} by {q.raisedByName} · hold {q.holdDays} working day(s)
                                </span>
                                <Pill tone={q.status === 'Open' ? 'warning' : q.status === 'Responded' ? 'info' : 'success'}>{q.status}</Pill>
                                {resolved && (
                                    <button type="button" className="link-button" onClick={() => setExpanded((e) => ({ ...e, [q.id]: !show }))}>
                                        {show ? 'Hide thread' : `Show thread (${q.messages.length})`}
                                    </button>
                                )}
                            </header>
                            {show && (
                                <ol className="messages">
                                    {q.messages.map((m) => (
                                        <li key={m.id} className={`message ${m.authorTeam === q.fromTeam ? 'from' : 'to'}`}>
                                            <div className="message-meta">
                                                <strong>{m.authorName}</strong> · {m.authorTeam} · {m.messageType} · {dateTime(m.createdAt)}
                                                {m.aiAssisted && <span className="ai-tag">✦ AI-assisted</span>}
                                            </div>
                                            <p>{m.body}</p>
                                        </li>
                                    ))}
                                </ol>
                            )}
                            {show && reviewInsight && (
                                <div className="thread-ai">
                                    <AiInsightNote insight={reviewInsight} />
                                    {target && reviewInsight.suggested?.details && (
                                        <button
                                            type="button"
                                            className="primary-button ai-primary"
                                            onClick={() =>
                                                onReview(target, { details: reviewInsight.suggested!.details!, ...(reviewInsight.suggested!.discrepancyType ? { discrepancyType: reviewInsight.suggested!.discrepancyType } : {}) }, reviewInsight.id)
                                            }
                                        >
                                            Review &amp; {target.label.charAt(0).toLowerCase() + target.label.slice(1)}
                                        </button>
                                    )}
                                </div>
                            )}
                            {show && isOps && ai.allowed && q.toTeam === 'CSE' && q.status === 'Responded' && (
                                <button type="button" className="ai-button" disabled={busy !== null || !ai.ready} onClick={() => void review(q)}>
                                    {busy === q.id ? 'Reviewing…' : reviewInsight ? 'Review reply with AI again' : '✦ Review reply with AI'}
                                </button>
                            )}
                        </article>
                    )
                })}
            </div>
        </section>
    )
}

function RecordsTab({ detail }: { detail: CaseDetail }) {
    const a = detail.latest
    return (
        <>
            <div className="overview-grid">
                <section className="panel overview-main">
                    <div className="panel-heading">
                        <div>
                            <h2>Stage dates</h2>
                            <p>Dates captured on the latest record (record #{a.id}, {a.entryType}).</p>
                        </div>
                    </div>
                    <StageDates a={a} />
                </section>
                <section className="panel overview-side">
                    <div className="panel-heading">
                        <div>
                            <h2>Auto-calculated &amp; tracking fields</h2>
                            <p>Calculated by the system (working days, Mon–Fri)</p>
                        </div>
                    </div>
                    <KeyValue
                        items={[
                            ['Original Inward Date', date(detail.originalInwardDate)],
                            ['Resubmission Count', String(detail.resubmissionCount)],
                            ['Current Stage', `${a.currentStage}${a.isCurrentStageManuallyOverridden ? ' (set manually)' : ''}`],
                            ['RFT Status', a.rftStatus],
                            ['Gross TAT', `${number(a.grossTatDays)} days`],
                            ['Query Hold Days', `${number(a.queryHoldDays)} days`],
                            ['Net TAT', `${number(a.netTatDays)} days`],
                            ['Current Aging', a.accountOpeningDate ? 'Closed' : `${number(a.currentAgingDays)} days`],
                            ['SLA Breach (overall)', `${a.isSlaBreached ? 'Yes' : 'No'} · threshold ${detail.slaThresholdWorkingDays} days`],
                            ['SLA Breach (stage-wise)', (a.stageTats ?? []).filter((t) => t.isSlaBreached).map((t) => t.stage).join(', ') || 'None'],
                            ['Query Count', String(a.queryCount)],
                            ['Touch Count', String(a.touchCount)],
                            ['Outward Date', date(a.outwardDate)],
                            ['Resubmission Date', date(a.resubmissionDate)],
                            ['Query Details', a.queryDetails || '—'],
                            ['Remarks', a.remarks || '—'],
                        ]}
                    />
                </section>
            </div>
            <section className="panel register-panel">
                <div className="panel-heading register-heading">
                    <div>
                        <h2>Records for this Reference ID</h2>
                        <p>Resubmissions and discrepancy resolutions are separate records sharing the Reference ID.</p>
                    </div>
                    <span className="panel-tag">{detail.records.length}</span>
                </div>
                <div className="table-scroll">
                    <table>
                        <thead>
                            <tr>
                                <th>Record</th>
                                <th>Entry Type</th>
                                <th>Inward Date</th>
                                <th>Latest Status</th>
                                <th>Current Stage</th>
                                <th>Query Hold Days</th>
                                <th>Touch Count</th>
                            </tr>
                        </thead>
                        <tbody>
                            {detail.records.map((r) => (
                                <tr key={r.id}>
                                    <td>
                                        <strong>#{r.id}</strong>
                                        {r.isLatestRecord && <span className="cell-subtitle">latest</span>}
                                    </td>
                                    <td>{r.entryType}</td>
                                    <td>{date(r.inwardDate)}</td>
                                    <td>
                                        <StatusPill status={r.latestStatus} />
                                    </td>
                                    <td>{r.currentStage}</td>
                                    <td>{r.queryHoldDays} d</td>
                                    <td>{r.touchCount}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            </section>
        </>
    )
}

type HistoryItem = { key: string; at: string; ai: boolean; node: ReactNode }

function HistoryTab({ detail }: { detail: CaseDetail }) {
    const [filter, setFilter] = useState<'all' | 'people' | 'ai'>('all')
    const items: HistoryItem[] = [
        ...detail.history.map((e) => ({
            key: `e${e.id}`,
            at: e.createdAt,
            ai: e.userName === 'AccountFlow AI',
            node: (
                <>
                    <div className="timeline-head">
                        <strong>{e.action}</strong>
                        <span className="muted small">{dateTime(e.createdAt)}</span>
                    </div>
                    <div className="small">
                        {e.userName}
                        {e.team ? ` · ${e.team}` : ''}
                        {e.toStatus && (
                            <>
                                {' '}
                                · {e.fromStatus ? `${e.fromStatus} → ` : ''}
                                {e.toStatus}
                            </>
                        )}
                    </div>
                    {e.details && <div className="muted small">{e.details}</div>}
                </>
            ),
        })),
        ...detail.aiInsights.map((i: AiInsight) => ({
            key: `i${i.id}`,
            at: i.createdAt,
            ai: true,
            node: (
                <>
                    <div className="timeline-head">
                        <strong>
                            ✦ {KIND_LABEL[i.kind] ?? i.kind}
                            {i.queryId ? ` · query #${i.queryId}` : ''}
                        </strong>
                        <span className="muted small">{dateTime(i.createdAt)}</span>
                    </div>
                    <div className="small">
                        <Pill tone={OUTCOME_TONE[i.outcome] ?? 'neutral'}>{OUTCOME_LABEL[i.outcome] ?? i.outcome}</Pill>{' '}
                        {showConfidence(i) && <span className="muted">confidence {Math.round(i.confidence * 100)}% · </span>}
                        <span className="muted">
                            {i.provider} · {i.model} · mode {i.mode}
                        </span>
                    </div>
                    <div className="small">{i.summary}</div>
                    {i.findings.length > 0 && (
                        <ul className="small">
                            {i.findings.map((f) => (
                                <li key={f}>{f}</li>
                            ))}
                        </ul>
                    )}
                    {i.actionResult && (
                        <div className="muted small">
                            {i.actionStatus === 'Pending' ? '◷ ' : i.actionStatus === 'Applied' ? '✓ ' : i.actionStatus === 'Dismissed' ? '✕ Dismissed · ' : ''}
                            {i.actionResult}
                        </div>
                    )}
                </>
            ),
        })),
    ]
        .filter((x) => filter === 'all' || (filter === 'ai' ? x.ai : !x.ai))
        .sort((x, y) => new Date(y.at.endsWith('Z') ? y.at : `${y.at}Z`).getTime() - new Date(x.at.endsWith('Z') ? x.at : `${x.at}Z`).getTime())

    return (
        <section className="panel">
            <div className="register-tools">
                <div className="segmented">
                    {(['all', 'people', 'ai'] as const).map((f) => (
                        <button key={f} className={filter === f ? 'active' : ''} onClick={() => setFilter(f)}>
                            {f === 'all' ? 'All' : f === 'people' ? 'People' : 'AI only'}
                        </button>
                    ))}
                </div>
            </div>
            {items.length === 0 ? (
                <Empty>No recorded events yet.</Empty>
            ) : (
                <ol className="timeline">
                    {items.map((x) => (
                        <li key={x.key} className={x.ai ? 'timeline-ai' : undefined}>
                            {x.node}
                        </li>
                    ))}
                </ol>
            )}
        </section>
    )
}

function StageDates({ a }: { a: Account }) {
    const physical = a.channel?.toLowerCase() !== 'digital'
    const rows: [string, [string, string][]][] = [
        ['Stage 1 — CSE Request to Operations', [
            ['Inward Date', date(a.inwardDate)],
            ['Stage 1 Query Raised Date', date(a.stage1QueryRaisedDate)],
            ['Stage 1 Query Details', a.stage1QueryDetails || '—'],
            ['Stage 1 Resubmission Date', date(a.stage1ResubmissionDate)],
            ['Stage status', a.stage1Status],
        ]],
        ['Stage 2 — Form Preparation & Submission', [
            ['Form Prepared Date', date(a.formPreparedDate)],
            physical ? ['Physical Form Submitted to CSE Date', date(a.physicalFormSubmittedToCseDate)] : ['Digital Form Sent to Client Date', date(a.digitalFormSentToClientDate)],
            ['Stage status', a.stage2Status],
        ]],
        ['Stage 3 — Receipt of Signed Form', [
            ['Signed Form Received Date', date(a.signedFormReceivedDate)],
            ['Discrepancy Raised Date', date(a.discrepancyRaisedDate)],
            ['Discrepancy Type', a.discrepancyType ?? '—'],
            ['Form Returned to CSE Date', date(a.formReturnedToCseDate)],
            ['Stage status', a.stage3Status],
        ]],
        ['Stage 4 — Resubmission / Discrepancy Resolution', [
            ['Discrepancy Resolution Received Date', date(a.discrepancyResolutionReceivedDate)],
            ['Resubmitted Form Received Date', date(a.resubmittedFormReceivedDate)],
            ['Stage 4 Review Outcome', a.stage4ReviewOutcome ?? '—'],
            ['Stage status', a.stage4Status],
        ]],
        ['Stage 5 — Submission to MOFSL', [
            ['Submitted to MOFSL Date', date(a.submittedToMofslDate)],
            ['MOFSL Query Raised Date', date(a.mofslQueryRaisedDate)],
            ['MOFSL Query Type', a.mofslQueryType ?? '—'],
            ['MOFSL Query Resolved Date', date(a.mofslQueryResolvedDate)],
            ['Stage status', a.stage5Status],
        ]],
        ['Stage 6 — Account Opened & Communication', [
            ['Account Number', a.accountNumber ?? '—'],
            ['Account Opening Date', date(a.accountOpeningDate)],
            ['Communication Sent Date', date(a.communicationSentDate)],
            ['Stage status', a.stage6Status],
        ]],
    ]
    return (
        <div className="stage-dates">
            {rows.map(([title, items]) => (
                <div key={title}>
                    <h3>{title}</h3>
                    <KeyValue items={items} />
                </div>
            ))}
            {a.remarks && (
                <div>
                    <h3>Remarks</h3>
                    <p className="small">{a.remarks}</p>
                </div>
            )}
        </div>
    )
}

function EditDetails({ detail, onClose, onSaved }: { detail: CaseDetail; onClose: () => void; onSaved: (d: CaseDetail) => void }) {
    const a = detail.latest
    const [lookups, setLookups] = useState<Lookups | null>(null)
    const [form, setForm] = useState({
        clientName: a.clientName,
        panNo: a.panNo,
        accountType: a.accountType,
        channel: a.channel,
        location: a.location,
        segment: a.segment,
        cse: a.cse,
        relationshipManager: a.relationshipManager,
        processor: a.processor,
        outwardDate: a.outwardDate?.slice(0, 10) ?? '',
        queryHoldDays: String(a.queryHoldDays),
        remarks: a.remarks ?? '',
        currentStageOverride: a.isCurrentStageManuallyOverridden ? a.currentStage : '',
    })
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)

    useEffect(() => {
        get<Lookups>('/lookups').then(setLookups).catch(() => undefined)
    }, [])

    const set = (k: keyof typeof form, v: string) => setForm((f) => ({ ...f, [k]: v }))

    async function submit(e: FormEvent) {
        e.preventDefault()
        setBusy(true)
        setError('')
        try {
            const updated = await put<CaseDetail>(`/cases/${encodeURIComponent(detail.referenceId)}/details`, {
                ...form,
                panNo: form.panNo.toUpperCase(),
                outwardDate: form.outwardDate || null,
                queryHoldDays: Number(form.queryHoldDays) || 0,
                remarks: form.remarks || null,
                currentStageOverride: form.currentStageOverride || null,
            })
            onSaved(updated)
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    const formSent = !!(a.formPreparedDate || a.physicalFormSubmittedToCseDate || a.digitalFormSentToClientDate)

    return (
        <Modal title="Edit case details" onClose={onClose} wide>
            {a.isMasked ? (
                <p>You cannot edit details while client identifiers are masked for your role.</p>
            ) : (
                <form onSubmit={submit} className="form-grid">
                    <Field label="Client Name *">
                        <input value={form.clientName} onChange={(e) => set('clientName', e.target.value)} required maxLength={200} />
                    </Field>
                    <Field label="PAN No *" help="Format AAAAA9999A">
                        <input value={form.panNo} onChange={(e) => set('panNo', e.target.value.toUpperCase())} required maxLength={10} pattern="[A-Za-z]{5}[0-9]{4}[A-Za-z]" />
                    </Field>
                    <Field label="Account Type">
                        <select value={form.accountType} onChange={(e) => set('accountType', e.target.value)}>
                            {(lookups?.accountTypes ?? [form.accountType]).map((o) => (
                                <option key={o}>{o}</option>
                            ))}
                        </select>
                    </Field>
                    <Field label="Channel" help={formSent ? 'Locked once the form has been sent.' : null}>
                        <select value={form.channel} onChange={(e) => set('channel', e.target.value)} disabled={formSent}>
                            {(lookups?.channels ?? [form.channel]).map((o) => (
                                <option key={o}>{o}</option>
                            ))}
                        </select>
                    </Field>
                    <Field label="Location">
                        <input value={form.location} onChange={(e) => set('location', e.target.value)} list="lk-locations" maxLength={100} />
                    </Field>
                    <Field label="Segment">
                        <input value={form.segment} onChange={(e) => set('segment', e.target.value)} list="lk-segments" maxLength={100} />
                    </Field>
                    <Field label="CSE Name">
                        <input value={form.cse} onChange={(e) => set('cse', e.target.value)} list="lk-cses" maxLength={150} />
                    </Field>
                    <Field label="RM Name">
                        <input value={form.relationshipManager} onChange={(e) => set('relationshipManager', e.target.value)} maxLength={150} />
                    </Field>
                    <Field label="Processor Name">
                        <input value={form.processor} onChange={(e) => set('processor', e.target.value)} maxLength={150} />
                    </Field>
                    <Field label="Outward Date">
                        <input type="date" value={form.outwardDate} onChange={(e) => set('outwardDate', e.target.value)} />
                    </Field>
                    <Field label="Query Hold Days (cumulative)" help="Workflow responses add hold days automatically; adjust only to correct.">
                        <input type="number" min={0} max={3650} value={form.queryHoldDays} onChange={(e) => set('queryHoldDays', e.target.value)} />
                    </Field>
                    <Field label="Current Stage" help="Derived from the stage dates. Override only to correct it; the next workflow step switches it back to automatic.">
                        <select value={form.currentStageOverride} onChange={(e) => set('currentStageOverride', e.target.value)}>
                            <option value="">Automatic ({a.isCurrentStageManuallyOverridden ? 'from stage dates' : a.currentStage})</option>
                            {(lookups?.stages ?? []).map((st) => (
                                <option key={st} value={st}>
                                    {st} (manual)
                                </option>
                            ))}
                        </select>
                    </Field>
                    <Field label="Remarks">
                        <textarea rows={3} value={form.remarks} onChange={(e) => set('remarks', e.target.value)} maxLength={2000} />
                    </Field>
                    <datalist id="lk-locations">{lookups?.locations.map((o) => <option key={o} value={o} />)}</datalist>
                    <datalist id="lk-segments">{lookups?.segmentOptions.map((o) => <option key={o} value={o} />)}</datalist>
                    <datalist id="lk-cses">{lookups?.cses.map((o) => <option key={o} value={o} />)}</datalist>
                    {error && <div className="error-banner span-all">{error}</div>}
                    <div className="form-actions span-all">
                        <button type="button" className="secondary-button" onClick={onClose}>
                            Cancel
                        </button>
                        <button type="submit" className="primary-button" disabled={busy}>
                            {busy ? 'Saving…' : 'Save details'}
                        </button>
                    </div>
                </form>
            )}
        </Modal>
    )
}
