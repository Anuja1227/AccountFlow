import { useCallback, useEffect, useState } from 'react'
import { MisInsightsPanel } from '../components/AiPanels'
import { ErrorBanner, PageHeader } from '../components/ui'
import { errorMessage, get } from '../lib/api'
import { number } from '../lib/format'
import type { Breakdown, Metrics, MisData } from '../lib/types'

const emptyMetrics: Metrics = {
    totalRecords: 0,
    totalCases: 0,
    resubmissionRecords: 0,
    openCases: 0,
    completedCases: 0,
    rftCases: 0,
    nrftCases: 0,
    rftPercentage: 0,
    averageGrossTatDays: 0,
    averageNetTatDays: 0,
    averageCurrentAgingDays: 0,
    slaBreachCases: 0,
    slaBreachRecords: 0,
    stage1QueryCases: 0,
    stage3DiscrepancyCases: 0,
    mofslQueryCases: 0,
    cseSideReworkCases: 0,
    mofslSideReworkCases: 0,
    stageSlaBreachCases: 0,
}

const stages = ['Stage 1', 'Stage 2', 'Stage 3', 'Stage 4', 'Stage 5', 'Stage 6', 'Completed']

export function MisDashboard() {
    const [mis, setMis] = useState<MisData | null>(null)
    const [cse, setCse] = useState('')
    const [location, setLocation] = useState('')
    const [segment, setSegment] = useState('')
    const [options, setOptions] = useState<{ cse: string[]; location: string[]; segment: string[] }>({ cse: [], location: [], segment: [] })
    const [dimension, setDimension] = useState<'byCse' | 'byLocation' | 'bySegment'>('byCse')
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            const params = new URLSearchParams()
            if (cse) params.set('cse', cse)
            if (location) params.set('location', location)
            if (segment) params.set('segment', segment)
            const data = await get<MisData>(`/accounts/mis?${params.toString()}`)
            setMis(data)
            // Keep the full option lists from the unfiltered view.
            if (!cse && !location && !segment) {
                const names = (b: Breakdown[]) => b.map((x) => x.name).filter((n) => n !== 'Unassigned')
                setOptions({ cse: names(data.byCse), location: names(data.byLocation), segment: names(data.bySegment) })
            }
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [cse, location, segment])

    useEffect(() => {
        void load()
    }, [load])

    const summary = mis?.summary ?? emptyMetrics
    const pipeline = stages.map((stage) => {
        const item = mis?.stagePipeline.find((s) => s.stage === stage)
        return {
            stage,
            caseCount: item?.caseCount ?? 0,
            averageAgingDays: item?.averageAgingDays ?? 0,
            sla: item?.slaBreachCases ?? 0,
            measured: item?.casesMeasured ?? 0,
            stageTat: item?.averageStageTatDays ?? 0,
            stageSla: item?.stageSlaBreachCases ?? 0,
        }
    })
    const maxCases = Math.max(1, ...pipeline.map((s) => s.caseCount))

    return (
        <>
            <PageHeader
                crumb="Reports / MIS"
                title="Account Opening Dashboard"
                subtitle="Monitor onboarding requests, turnaround time and operational performance."
                actions={
                    <button className="refresh-button" onClick={() => void load()} disabled={loading}>
                        ↻ Refresh data
                    </button>
                }
            />

            {error && <ErrorBanner message={error} onRetry={() => void load()} />}

            <section className="filters-panel">
                <div className="filter-heading">
                    <div>
                        <h2>Filter reports</h2>
                        <p>Filters apply to every metric, breakdown, the pipeline and the AI commentary</p>
                    </div>
                    <button
                        className="clear-button"
                        onClick={() => {
                            setCse('')
                            setLocation('')
                            setSegment('')
                        }}
                    >
                        Clear filters
                    </button>
                </div>

                <div className="filter-grid">
                    <label>
                        CSE
                        <select value={cse} onChange={(e) => setCse(e.target.value)}>
                            <option value="">All CSEs</option>
                            {options.cse.map((n) => (
                                <option key={n} value={n}>
                                    {n}
                                </option>
                            ))}
                        </select>
                    </label>
                    <label>
                        Location
                        <select value={location} onChange={(e) => setLocation(e.target.value)}>
                            <option value="">All locations</option>
                            {options.location.map((n) => (
                                <option key={n} value={n}>
                                    {n}
                                </option>
                            ))}
                        </select>
                    </label>
                    <label>
                        Segment
                        <select value={segment} onChange={(e) => setSegment(e.target.value)}>
                            <option value="">All segments</option>
                            {options.segment.map((n) => (
                                <option key={n} value={n}>
                                    {n}
                                </option>
                            ))}
                        </select>
                    </label>
                </div>
            </section>

            <div className="section-heading">
                <div>
                    <h2>Performance overview</h2>
                    <p>
                        Calculated by the backend · SLA threshold {mis?.slaThresholdWorkingDays ?? '—'} working days
                    </p>
                </div>
                <span className="live-badge">● {loading ? 'Loading…' : 'Live API data'}</span>
            </div>

            <section className="metrics-grid">
                <MetricCard title="Total cases" value={number(summary.totalCases)} detail={`${number(summary.totalRecords)} records incl. ${number(summary.resubmissionRecords)} resubmission rows`} tone="blue" />
                <MetricCard title="RFT cases" value={number(summary.rftCases)} detail={`${number(summary.rftPercentage)}% right first time`} tone="green" />
                <MetricCard title="NRFT cases" value={number(summary.nrftCases)} detail={`CSE-side ${number(summary.cseSideReworkCases)} · MOFSL-side ${number(summary.mofslSideReworkCases)}`} tone="orange" />
                <MetricCard title="Average gross TAT" value={`${number(summary.averageGrossTatDays)} days`} detail={`Net TAT: ${number(summary.averageNetTatDays)} days (per case)`} tone="purple" />
                <MetricCard title="SLA breaches" value={number(summary.slaBreachCases)} detail={`Cases beyond SLA · ${number(summary.stageSlaBreachCases)} with a stage over SLA`} tone="red" />
                <MetricCard title="Open cases" value={number(summary.openCases)} detail={`Avg. current aging ${number(summary.averageCurrentAgingDays)} days · ${number(summary.completedCases)} opened`} tone="blue" />
                <MetricCard title="Stage 1 queries" value={number(summary.stage1QueryCases)} detail="Cases with missing-information queries" tone="blue" />
                <MetricCard title="Stage 3 discrepancies" value={number(summary.stage3DiscrepancyCases)} detail={`Cases with discrepancies/returns · MOFSL queries: ${number(summary.mofslQueryCases)}`} tone="orange" />
            </section>

            <section className="content-grid">
                <div className="panel">
                    <div className="panel-heading">
                        <div>
                            <h2>Workflow pipeline</h2>
                            <p>Cases by current stage, with average working days spent in each stage</p>
                        </div>
                        <span className="panel-tag">7 stages</span>
                    </div>
                    <div className="pipeline-list">
                        {pipeline.map((item, index) => (
                            <div className="pipeline-row" key={item.stage}>
                                <div className="pipeline-label">
                                    <span className={`stage-dot stage-${index}`} />
                                    <span>{item.stage}</span>
                                    <strong>{item.caseCount}</strong>
                                </div>
                                <div className="progress-track">
                                    <div className={`progress-fill stage-${index}`} style={{ width: `${(item.caseCount / maxCases) * 100}%` }} />
                                </div>
                                <div className="stage-aging">
                                    <span>Avg. aging</span>
                                    <strong>{number(item.averageAgingDays)} days</strong>
                                    {item.sla > 0 && <span className="sla-chip">{item.sla} SLA</span>}
                                </div>
                                {item.stage !== 'Completed' && (
                                    <div className="stage-aging stage-tat" title="Stage TAT: working days spent in this stage, across every case that reached it">
                                        <span>Avg. in stage</span>
                                        <strong>{item.measured > 0 ? `${number(item.stageTat)} days` : '—'}</strong>
                                        <span>({item.measured} case{item.measured === 1 ? '' : 's'})</span>
                                        {item.stageSla > 0 && <span className="sla-chip">{item.stageSla} over SLA in stage</span>}
                                    </div>
                                )}
                            </div>
                        ))}
                    </div>
                </div>

                <div className="panel">
                    <div className="panel-heading">
                        <div>
                            <h2>Business breakdown</h2>
                            <p>Case distribution by dimension</p>
                        </div>
                    </div>
                    <BreakdownList title="By CSE" data={mis?.byCse ?? []} />
                    <BreakdownList title="By location" data={mis?.byLocation ?? []} />
                    <BreakdownList title="By vertical / segment" data={mis?.bySegment ?? []} />
                </div>
            </section>

            <section className="panel register-panel">
                <div className="panel-heading register-heading">
                    <div>
                        <h2>MIS by dimension</h2>
                        <p>RFT, TAT, SLA and rework attribution (CSE-side vs MOFSL-side)</p>
                    </div>
                    <div className="segmented" role="tablist">
                        {(
                            [
                                ['byCse', 'CSE'],
                                ['byLocation', 'Location'],
                                ['bySegment', 'Vertical / Segment'],
                            ] as const
                        ).map(([key, label]) => (
                            <button key={key} role="tab" aria-selected={dimension === key} className={dimension === key ? 'active' : ''} onClick={() => setDimension(key)}>
                                {label}
                            </button>
                        ))}
                    </div>
                </div>
                <div className="table-scroll">
                    <table>
                        <thead>
                            <tr>
                                <th>Name</th>
                                <th>Cases</th>
                                <th>Records</th>
                                <th>RFT %</th>
                                <th>NRFT</th>
                                <th>Avg gross TAT</th>
                                <th>Avg net TAT</th>
                                <th>SLA breaches</th>
                                <th>Stage-wise SLA breaches</th>
                                <th>Stage 1 queries</th>
                                <th>Stage 3 discrepancies</th>
                                <th>MOFSL queries</th>
                            </tr>
                        </thead>
                        <tbody>
                            {(mis?.[dimension] ?? []).map((b) => (
                                <tr key={b.name}>
                                    <td>
                                        <strong>{b.name}</strong>
                                    </td>
                                    <td>{number(b.metrics.totalCases)}</td>
                                    <td>{number(b.metrics.totalRecords)}</td>
                                    <td>{number(b.metrics.rftPercentage)}%</td>
                                    <td>{number(b.metrics.nrftCases)}</td>
                                    <td>{number(b.metrics.averageGrossTatDays)} d</td>
                                    <td>{number(b.metrics.averageNetTatDays)} d</td>
                                    <td>{number(b.metrics.slaBreachCases)}</td>
                                    <td>{number(b.metrics.stageSlaBreachCases)}</td>
                                    <td>{number(b.metrics.stage1QueryCases)}</td>
                                    <td>{number(b.metrics.stage3DiscrepancyCases)}</td>
                                    <td>{number(b.metrics.mofslQueryCases)}</td>
                                </tr>
                            ))}
                            {(mis?.[dimension] ?? []).length === 0 && (
                                <tr>
                                    <td colSpan={12} className="empty-state">
                                        No data for the selected filters.
                                    </td>
                                </tr>
                            )}
                        </tbody>
                    </table>
                </div>
            </section>

            <MisInsightsPanel filters={{ cse, location, segment }} />

            <footer className="footer">
                <span>AccountFlow · Account Opening MIS</span>
                <span>Metrics calculated by the backend</span>
            </footer>
        </>
    )
}

function MetricCard({ title, value, detail, tone }: { title: string; value: string; detail: string; tone: string }) {
    return (
        <div className="metric-card">
            <div className={`metric-icon ${tone}`}>{tone === 'green' ? '✓' : tone === 'red' ? '!' : '◈'}</div>
            <div className="metric-title">{title}</div>
            <div className="metric-value">{value}</div>
            <div className="metric-detail">{detail}</div>
        </div>
    )
}

function BreakdownList({ title, data }: { title: string; data: Breakdown[] }) {
    return (
        <div className="breakdown-section">
            <h3>{title}</h3>
            {data.length === 0 ? (
                <p className="muted">No data available</p>
            ) : (
                data.map((item) => (
                    <div className="breakdown-row" key={item.name}>
                        <span>{item.name}</span>
                        <strong>{number(item.metrics.totalCases)} cases</strong>
                    </div>
                ))
            )}
        </div>
    )
}
