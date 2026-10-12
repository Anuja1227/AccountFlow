import { useCallback, useEffect, useMemo, useState } from 'react'
import { CaseTable } from '../components/CaseTable'
import { ErrorBanner, Loading, PageHeader } from '../components/ui'
import { errorMessage, get } from '../lib/api'
import { P, useAuth, WS } from '../lib/auth'
import { Link } from '../lib/router'
import type { Account } from '../lib/types'

const STAGES = ['Stage 1', 'Stage 2', 'Stage 3', 'Stage 4', 'Stage 5', 'Stage 6', 'Completed']

export function RegisterPage() {
    const { can, inWorkspace } = useAuth()
    const [rows, setRows] = useState<Account[]>([])
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')
    const [search, setSearch] = useState('')
    const [stage, setStage] = useState('')
    const [owner, setOwner] = useState('')
    const [showRecords, setShowRecords] = useState(false)
    const [openOnly, setOpenOnly] = useState(false)

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            setRows(await get<Account[]>(`/accounts?view=${showRecords ? 'records' : 'cases'}`))
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [showRecords])

    useEffect(() => {
        void load()
    }, [load])

    const filtered = useMemo(() => {
        const term = search.trim().toLowerCase()
        return rows.filter((a) => {
            if (stage && a.currentStage !== stage) return false
            if (owner && a.actionBy !== owner) return false
            if (openOnly && (a.accountOpeningDate || ['Communication Sent — Case Closed', 'Rejected', 'Cancelled by Client'].includes(a.latestStatus))) return false
            if (!term) return true
            return [a.referenceId, a.requestId, a.clientName, a.latestStatus, a.currentStage, a.cse, a.location, a.segment]
                .some((v) => String(v ?? '').toLowerCase().includes(term))
        })
    }, [rows, search, stage, owner, openOnly])

    return (
        <>
            <PageHeader
                crumb="Cases / Register"
                title="Account register"
                subtitle={
                    can(P.casesViewAll)
                        ? 'Every account-opening case. Click a reference to open the case.'
                        : 'Cases you are permitted to see. Click a reference to open the case.'
                }
                actions={
                    <>
                        {inWorkspace(WS.operations) && can(P.casesCreate) && (
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

            <section className="panel register-panel">
                <div className="register-tools">
                    <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search reference, client, status, CSE…" aria-label="Search cases" />
                    <select value={stage} onChange={(e) => setStage(e.target.value)} aria-label="Filter by stage">
                        <option value="">All stages</option>
                        {STAGES.map((s) => (
                            <option key={s}>{s}</option>
                        ))}
                    </select>
                    <select value={owner} onChange={(e) => setOwner(e.target.value)} aria-label="Filter by owner">
                        <option value="">Any owner</option>
                        <option>Operations</option>
                        <option>CSE</option>
                        <option>MOFSL</option>
                    </select>
                    <label className="checkbox">
                        <input type="checkbox" checked={openOnly} onChange={(e) => setOpenOnly(e.target.checked)} /> Open only
                    </label>
                    <label className="checkbox">
                        <input type="checkbox" checked={showRecords} onChange={(e) => setShowRecords(e.target.checked)} /> Show every record (incl. resubmissions)
                    </label>
                    <span className="muted small">
                        {filtered.length} {showRecords ? 'records' : 'cases'}
                    </span>
                </div>
                {loading ? <Loading label="Loading cases…" /> : <CaseTable rows={filtered} />}
            </section>
        </>
    )
}
