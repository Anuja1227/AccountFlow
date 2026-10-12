export function number(value: number | undefined | null) {
    return Number(value ?? 0).toLocaleString('en-IN', { maximumFractionDigits: 2 })
}

export function date(value: string | null | undefined) {
    if (!value) return '—'
    const d = new Date(value)
    if (Number.isNaN(d.getTime())) return '—'
    return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' })
}

export function dateTime(value: string | null | undefined) {
    if (!value) return '—'
    // Server timestamps are UTC without a suffix.
    const d = new Date(/[zZ]|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`)
    if (Number.isNaN(d.getTime())) return '—'
    return d.toLocaleString('en-IN', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

export function todayIso() {
    const d = new Date()
    const local = new Date(d.getTime() - d.getTimezoneOffset() * 60000)
    return local.toISOString().slice(0, 10)
}

export function toneForStatus(status: string): 'success' | 'warning' | 'danger' | 'neutral' | 'info' {
    if (/Account Opened|Case Closed|Found in Order/.test(status)) return 'success'
    if (/Query|Discrepancy|Returned|On Hold/.test(status)) return 'warning'
    if (/Rejected|Cancelled/.test(status)) return 'danger'
    if (/Submitted to MOFSL|Resubmitted/.test(status)) return 'info'
    return 'neutral'
}
