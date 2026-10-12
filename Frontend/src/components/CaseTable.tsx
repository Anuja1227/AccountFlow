import { number, date } from '../lib/format'
import { caseUrl, Link } from '../lib/router'
import type { Account } from '../lib/types'
import { Pill, StatusPill } from './ui'

export function CaseTable({ rows, emptyText = 'No matching cases.', showOwner = true }: { rows: Account[]; emptyText?: string; showOwner?: boolean }) {
    return (
        <div className="table-scroll">
            <table>
                <thead>
                    <tr>
                        <th>Reference ID / Request ID</th>
                        <th>Client Name</th>
                        <th>Entry Type</th>
                        <th>Current Stage</th>
                        <th>Latest Status</th>
                        {showOwner && <th>Action By</th>}
                        <th>RFT Status</th>
                        <th>Gross / Net TAT</th>
                        <th>Current Aging</th>
                        <th>SLA Breach</th>
                    </tr>
                </thead>
                <tbody>
                    {rows.map((a) => (
                        <tr key={a.id}>
                            <td>
                                <Link to={caseUrl(a.referenceId)} className="row-link">
                                    <strong>{a.referenceId || '—'}</strong>
                                </Link>
                                <span className="cell-subtitle">
                                    {a.requestId || '—'} · inward {date(a.inwardDate)}
                                </span>
                            </td>
                            <td>
                                <strong>{a.clientName || '—'}</strong>
                                <span className="cell-subtitle">
                                    {a.accountType} · {a.channel} · {a.segment || '—'}
                                </span>
                            </td>
                            <td>
                                {a.entryType}
                                {a.resubmissionCount > 0 && <span className="cell-subtitle">{a.resubmissionCount} resubmission(s)</span>}
                            </td>
                            <td>
                                <span className="stage-pill">{a.currentStage || '-'}</span>
                            </td>
                            <td>
                                <StatusPill status={a.latestStatus} />
                                {a.openQueryCount > 0 && <span className="cell-subtitle">{a.openQueryCount} open query(ies)</span>}
                            </td>
                            {showOwner && <td>{a.actionBy}</td>}
                            <td>
                                <Pill tone={a.rftStatus === 'RFT' ? 'success' : 'warning'}>{a.rftStatus}</Pill>
                            </td>
                            <td>
                                {number(a.grossTatDays)} / {number(a.netTatDays)} d
                            </td>
                            <td>{a.accountOpeningDate ? '—' : `${number(a.currentAgingDays)} d`}</td>
                            <td>
                                <Pill tone={a.isSlaBreached ? 'danger' : 'success'}>{a.isSlaBreached ? 'Breached' : 'Within SLA'}</Pill>
                                {(a.stageTats ?? []).some((t) => t.isSlaBreached) && (
                                    <span className="cell-subtitle">
                                        Over in {(a.stageTats ?? []).filter((t) => t.isSlaBreached).map((t) => t.stage).join(', ')}
                                    </span>
                                )}
                            </td>
                        </tr>
                    ))}
                    {rows.length === 0 && (
                        <tr>
                            <td colSpan={showOwner ? 10 : 9} className="empty-state">
                                {emptyText}
                            </td>
                        </tr>
                    )}
                </tbody>
            </table>
        </div>
    )
}
