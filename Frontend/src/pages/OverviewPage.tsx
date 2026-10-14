import { useCallback, useEffect, useState } from 'react'
import { ErrorBanner, PageHeader } from '../components/ui'
import { errorMessage, get } from '../lib/api'
import { number } from '../lib/format'
import { Link } from '../lib/router'
import type { OverviewData, TeamPending } from '../lib/types'

/**
 * Overall Summary Dashboard (requirements doc, section 7: "Organization-wide
 * KPIs for management reporting"). All figures come from GET /accounts/overview.
 */
export function OverviewPage() {
    const [data, setData] = useState<OverviewData | null>(null)
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            setData(await get<OverviewData>('/accounts/overview'))
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [])

    useEffect(() => {
        void load()
    }, [load])

    const s = data?.summary
    const r = data?.rework
    const stages = (data?.stagePipeline ?? []).filter((x) => x.stage !== 'Completed')
    const bottleneck = stages.filter((x) => x.casesMeasured > 0).sort((a, b) => b.averageStageTatDays - a.averageStageTatDays)[0]
    const pct = (n: number) => (r && r.totalCases > 0 ? Math.round((n * 100) / r.totalCases) : 0)

    return (
        <>
            <PageHeader
                crumb="Administration / Overview"
                title="Management overview"
                subtitle="Organisation-wide KPIs and where open cases are waiting."
                actions={
                    <button className="refresh-button" onClick={() => void load()} disabled={loading}>
                        ↻ Refresh data
                    </button>
                }
            />

            {error && <ErrorBanner message={error} onRetry={() => void load()} />}

            <section className="overview-kpis" aria-label="Key figures">
                <Kpi label="Open cases" value={number(s?.openCases)} note={`${number(s?.completedCases)} opened · ${number(s?.totalCases)} total`} />
                <Kpi label="Over SLA" value={number(s?.slaBreachCases)} note={`SLA ${data?.slaThresholdWorkingDays ?? '—'} working days`} tone={s && s.slaBreachCases > 0 ? 'bad' : undefined} />
                <Kpi label="RFT" value={`${number(s?.rftPercentage)}%`} note={`${number(s?.rftCases)} RFT · ${number(s?.nrftCases)} NRFT`} />
                <Kpi label="Avg. gross TAT" value={`${number(s?.averageGrossTatDays)} d`} note={`Net ${number(s?.averageNetTatDays)} d`} />
                <Kpi label="Avg. aging (open)" value={`${number(s?.averageCurrentAgingDays)} d`} note="Working days since inward" />
            </section>

            <div className="section-heading">
                <div>
                    <h2>Pending by team</h2>
                    <p>Open cases, grouped by who has to act next (Action By)</p>
                </div>
                <span className="live-badge">● {loading ? 'Loading…' : 'Live API data'}</span>
            </div>

            <section className="team-cards">
                {(data?.teams ?? []).map((t) => (
                    <TeamCard key={t.team} team={t} />
                ))}
            </section>

            <section className="panel register-panel">
                <div className="panel-heading">
                    <div>
                        <h2>Team performance</h2>
                        <p>Workload and query handling for each team</p>
                    </div>
                </div>
                <div className="table-scroll">
                    <table>
                        <thead>
                            <tr>
                                <th>Team</th>
                                <th>Pending now</th>
                                <th>Over SLA</th>
                                <th>Avg. aging</th>
                                <th>Oldest pending</th>
                                <th>Queries received</th>
                                <th>Waiting for reply</th>
                                <th>Avg. reply time</th>
                                <th>Queries raised</th>
                            </tr>
                        </thead>
                        <tbody>
                            {(data?.teams ?? []).map((t) => (
                                <tr key={t.team}>
                                    <td>
                                        <strong>{t.team}</strong>
                                    </td>
                                    <td>{number(t.pendingCases)}</td>
                                    <td>{number(t.overSlaCases)}</td>
                                    <td>{t.pendingCases > 0 ? `${number(t.averageAgingDays)} d` : '—'}</td>
                                    <td>{t.oldestReferenceId ? <Link to={`/cases/${encodeURIComponent(t.oldestReferenceId)}`}>{t.oldestReferenceId}</Link> : '—'}</td>
                                    <td>{number(t.queriesReceived)}</td>
                                    <td>{number(t.openQueries)}</td>
                                    <td>{replyTime(t.averageReplyHours)}</td>
                                    <td>{number(t.queriesRaised)}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            </section>

            <section className="content-grid">
                <div className="panel">
                    <div className="panel-heading">
                        <div>
                            <h2>Rework attribution</h2>
                            <p>Where NRFT came from: CSE-side (Stage 1 query, Stage 3 discrepancy or return) or MOFSL-side (Stage 5 query)</p>
                        </div>
                    </div>
                    {r && r.totalCases > 0 ? (
                        <>
                            <div className="rework-bar" role="img" aria-label={`RFT ${pct(r.rftCases)}%, CSE-side ${pct(r.cseSideOnlyCases)}%, MOFSL-side ${pct(r.mofslSideOnlyCases)}%, both ${pct(r.bothSidesCases)}%`}>
                                <span className="rw-rft" style={{ flexGrow: r.rftCases }} />
                                <span className="rw-cse" style={{ flexGrow: r.cseSideOnlyCases }} />
                                <span className="rw-mofsl" style={{ flexGrow: r.mofslSideOnlyCases }} />
                                <span className="rw-both" style={{ flexGrow: r.bothSidesCases }} />
                            </div>
                            <div className="rework-legend">
                                <ReworkRow cls="rw-rft" label="Right first time" n={r.rftCases} pct={pct(r.rftCases)} />
                                <ReworkRow cls="rw-cse" label="CSE-side only" n={r.cseSideOnlyCases} pct={pct(r.cseSideOnlyCases)} />
                                <ReworkRow cls="rw-mofsl" label="MOFSL-side only" n={r.mofslSideOnlyCases} pct={pct(r.mofslSideOnlyCases)} />
                                <ReworkRow cls="rw-both" label="Both sides" n={r.bothSidesCases} pct={pct(r.bothSidesCases)} />
                            </div>
                        </>
                    ) : (
                        <p className="muted">No cases yet.</p>
                    )}
                </div>

                <div className="panel">
                    <div className="panel-heading">
                        <div>
                            <h2>Stage-wise TAT</h2>
                            <p>{bottleneck ? `Slowest stage: ${bottleneck.stage} (${number(bottleneck.averageStageTatDays)} d on average)` : 'Average working days spent in each stage'}</p>
                        </div>
                    </div>
                    <div className="table-scroll">
                        <table className="compact-table">
                            <thead>
                                <tr>
                                    <th>Stage</th>
                                    <th>Cases now</th>
                                    <th>Avg. in stage</th>
                                    <th>Over SLA in stage</th>
                                </tr>
                            </thead>
                            <tbody>
                                {stages.map((x) => (
                                    <tr key={x.stage} className={bottleneck?.stage === x.stage ? 'bottleneck-row' : ''}>
                                        <td>
                                            <strong>{x.stage}</strong>
                                        </td>
                                        <td>{number(x.caseCount)}</td>
                                        <td>{x.casesMeasured > 0 ? `${number(x.averageStageTatDays)} d` : '—'}</td>
                                        <td>{number(x.stageSlaBreachCases)}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                </div>
            </section>

            <p className="overview-more">
                <Link to="/mis">Open the full MIS dashboard →</Link>
            </p>
        </>
    )
}

function replyTime(hours: number | null) {
    if (hours === null) return '—'
    if (hours < 24) return `${number(Math.round(hours * 10) / 10)} h`
    return `${number(Math.round((hours / 24) * 10) / 10)} d`
}

function Kpi({ label, value, note, tone }: { label: string; value: string; note: string; tone?: 'bad' }) {
    return (
        <div className={`overview-kpi ${tone ?? ''}`}>
            <span>{label}</span>
            <strong>{value}</strong>
            <small>{note}</small>
        </div>
    )
}

function TeamCard({ team: t }: { team: TeamPending }) {
    const queryLabel = t.team === 'MOFSL' ? 'Queries to answer' : t.team === 'CSE' ? 'Ops queries to answer' : 'MOFSL queries to answer'
    return (
        <div className="team-card">
            <div className="team-card-top">
                <strong>{t.team}</strong>
                {t.overSlaCases > 0 ? <span className="status-pill danger">{t.overSlaCases} over SLA</span> : <span className="status-pill success">Within SLA</span>}
            </div>
            <div className="team-card-count">
                {number(t.pendingCases)}
                <span>case{t.pendingCases === 1 ? '' : 's'} pending</span>
            </div>
            <dl>
                <div>
                    <dt>Avg. aging</dt>
                    <dd>{t.pendingCases > 0 ? `${number(t.averageAgingDays)} d` : '—'}</dd>
                </div>
                <div>
                    <dt>Oldest</dt>
                    <dd>{t.oldestReferenceId ? `${t.oldestReferenceId} · ${t.oldestAgingDays} d` : '—'}</dd>
                </div>
                <div>
                    <dt>{queryLabel}</dt>
                    <dd>{number(t.openQueries)}</dd>
                </div>
            </dl>
            {t.pendingCases > 0 && <Link to={`/register?owner=${encodeURIComponent(t.team)}&open=1`}>View {t.pendingCases} case{t.pendingCases === 1 ? '' : 's'}</Link>}
        </div>
    )
}

function ReworkRow({ cls, label, n, pct }: { cls: string; label: string; n: number; pct: number }) {
    return (
        <div className="rework-row">
            <span className={`rw-dot ${cls}`} />
            <span>{label}</span>
            <strong>
                {number(n)} <small>({pct}%)</small>
            </strong>
        </div>
    )
}
