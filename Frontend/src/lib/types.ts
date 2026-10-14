export type Team = { id: number; name: string; workspaceType: string }

export type Me = {
    id: number
    email: string
    fullName: string
    cseName: string | null
    roles: string[]
    teams: Team[]
    permissions: string[]
    workspaces: string[]
}

export type Account = {
    id: number
    referenceId: string
    requestId: string
    entryType: string
    clientName: string
    panNo: string
    accountType: string
    channel: string
    actionBy: string
    location: string
    segment: string
    cse: string
    relationshipManager: string
    processor: string
    inwardDate: string
    originalInwardDate: string | null
    outwardDate: string | null
    resubmissionDate: string | null
    stage1QueryRaisedDate: string | null
    stage1QueryDetails: string | null
    stage1ResubmissionDate: string | null
    stage1Status: string
    formPreparedDate: string | null
    physicalFormSubmittedToCseDate: string | null
    digitalFormSentToClientDate: string | null
    stage2Status: string
    signedFormReceivedDate: string | null
    discrepancyRaisedDate: string | null
    discrepancyType: string | null
    formReturnedToCseDate: string | null
    stage3Status: string
    discrepancyResolutionReceivedDate: string | null
    resubmittedFormReceivedDate: string | null
    stage4ReviewOutcome: string | null
    stage4Status: string
    submittedToMofslDate: string | null
    mofslQueryRaisedDate: string | null
    mofslQueryType: string | null
    mofslQueryResolvedDate: string | null
    stage5Status: string
    accountNumber: string | null
    mofslAccountNumber: string | null
    mofslAccountOpeningDate: string | null
    mofslAccountDetails: string | null
    accountOpeningDate: string | null
    stage6Status: string
    communicationSentDate: string | null
    latestStatus: string
    currentStage: string
    isCurrentStageManuallyOverridden: boolean
    rftStatus: string
    queryCount: number
    queryDetails: string | null
    queryHoldDays: number
    touchCount: number
    remarks: string | null
    grossTatDays: number
    netTatDays: number
    currentAgingDays: number
    isSlaBreached: boolean
    stageTats: StageTat[]
    resubmissionCount: number
    recordCount: number
    isLatestRecord: boolean
    isMasked: boolean
    statusStage: string
    openQueryCount: number
}

export type QueryMessage = {
    id: number
    authorName: string
    authorTeam: string
    messageType: string
    body: string
    aiAssisted: boolean
    createdAt: string
}

export type CaseQuery = {
    id: number
    accountId: number
    referenceId: string
    stage: string
    queryType: string
    queryTypeLabel: string
    category: string | null
    fromTeam: string
    toTeam: string
    status: string
    raisedByName: string
    raisedAt: string
    respondedAt: string | null
    resolvedAt: string | null
    holdDays: number
    followUpCount: number
    caseStatus: string | null
    clientName: string | null
    messages: QueryMessage[]
}

export type AuditEvent = {
    id: number
    accountId: number | null
    referenceId: string | null
    userName: string
    team: string | null
    action: string
    fromStatus: string | null
    toStatus: string | null
    details: string | null
    createdAt: string
}

export type ActionField = {
    name: string
    label: string
    type: 'text' | 'textarea' | 'select' | 'date' | 'files'
    required: boolean
    options: string[] | null
    help: string | null
}

export type AvailableAction = {
    code: string
    label: string
    team: string
    description: string
    tone: string
    queryId: number | null
    queryLabel: string | null
    fields: ActionField[]
}

export type AiInsight = {
    id: number
    kind: 'IntakeCheck' | 'ResponseReview' | string
    queryId: number | null
    outcome: string
    confidence: number
    summary: string
    findings: string[]
    recommendedAction: string | null
    mode: string
    actionStatus: 'Pending' | 'Applied' | 'Dismissed' | 'NotApplicable' | 'Blocked' | string
    actionResult: string | null
    appliedByName: string | null
    appliedAt: string | null
    provider: string
    model: string
    createdAt: string
    suggested: { action: string; queryId: number | null; discrepancyType: string | null; details: string | null; firstAction: string; firstQueryId: number | null; stepCount: number } | null
}

export type CaseDetail = {
    referenceId: string
    latest: Account
    records: Account[]
    queries: CaseQuery[]
    history: AuditEvent[]
    availableActions: AvailableAction[]
    aiInsights: AiInsight[]
    attachments: CaseAttachment[]
    documents: CaseDocument[]
    applicationFormStatus: 'Draft' | 'Sent' | null
    formNotReadyReason: string | null
    automationMode: string
    originalInwardDate: string | null
    resubmissionCount: number
    slaThresholdWorkingDays: number
    canEditDetails: boolean
    canOverride: boolean
}

export type Metrics = {
    totalRecords: number
    totalCases: number
    resubmissionRecords: number
    openCases: number
    completedCases: number
    rftCases: number
    nrftCases: number
    rftPercentage: number
    averageGrossTatDays: number
    averageNetTatDays: number
    averageCurrentAgingDays: number
    slaBreachCases: number
    slaBreachRecords: number
    stage1QueryCases: number
    stage3DiscrepancyCases: number
    mofslQueryCases: number
    cseSideReworkCases: number
    mofslSideReworkCases: number
    stageSlaBreachCases: number
}

export type Breakdown = { name: string; metrics: Metrics }

export type PipelineStage = {
    stage: string
    caseCount: number
    averageAgingDays: number
    slaBreachCases: number
    /** Cases that reached this stage (Stage 1–6 TAT measured). */
    casesMeasured: number
    averageStageTatDays: number
    /** Cases whose time in this stage exceeded the SLA (stage-wise SLA). */
    stageSlaBreachCases: number
}

/** Working days a case spent in one stage (Stage 1–6 TAT). */
export type StageTat = {
    stage: string
    startDate: string | null
    endDate: string | null
    days: number | null
    inProgress: boolean
    isSlaBreached: boolean
}

export type MisData = {
    slaThresholdWorkingDays: number
    summary: Metrics
    byCse: Breakdown[]
    byLocation: Breakdown[]
    bySegment: Breakdown[]
    stagePipeline: PipelineStage[]
}

export type Lookups = {
    statuses: { status: string; stage: string }[]
    stages: string[]
    entryTypes: string[]
    accountTypes: string[]
    channels: string[]
    segmentOptions: string[]
    discrepancyTypes: string[]
    cses: string[]
    locations: string[]
    segments: string[]
    slaThresholdWorkingDays: number
}

export type AiHealth = {
    provider: string
    model: string
    configured: boolean
    reachable: boolean
    external: boolean
    message: string
}

export type RequestAttachment = { id: number; fileName: string; contentType: string; sizeBytes: number; uploadedAt: string }

export type CaseAttachment = RequestAttachment & { requestId: number }

export type CaseRequest = {
    id: number
    cseUserName: string
    cseName: string
    details: string
    status: 'Submitted' | 'Logged' | string
    submittedAt: string
    referenceId: string | null
    loggedByName: string | null
    loggedAt: string | null
    attachments: RequestAttachment[]
}

export type CaseDocument = {
    id: number
    queryId: number | null
    purpose: string
    fileName: string
    contentType: string
    sizeBytes: number
    uploadedByName: string
    team: string
    uploadedAt: string
}

export type FormField = {
    key: string
    label: string
    section: string
    required: boolean
    type: 'text' | 'date' | 'select' | 'multi' | 'tel' | 'email'
    options: string[] | null
    help: string | null
    minorOnly: boolean
}

export type ApplicationForm = {
    referenceId: string
    saved: boolean
    status: 'Draft' | 'Sent'
    data: Record<string, string>
    aiFilled: string[]
    missing: string[]
    errors: string[]
    updatedByName: string | null
    updatedAt: string | null
    sentAt: string | null
    canEdit: boolean
    case: {
        referenceId: string
        requestId: string
        clientName: string
        panNo: string
        accountType: string
        channel: string
        segment: string
        location: string
        relationshipManager: string
        cse: string
        latestStatus: string
    }
    fields: FormField[]
}
