export type BuildStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Interrupted'

export type BuildPhase =
  | 'Queued'
  | 'SourceSync'
  | 'Build'
  | 'Cook'
  | 'Stage'
  | 'Package'
  | 'Archive'
  | 'Zip'
  | 'Completed'
  | 'Failed'
  | 'Interrupted'

export type BuildTargetType = 'Game' | 'Client' | 'Server'

export type BuildTriggerSource = 'Manual' | 'Schedule'

export type BuildScheduleScopeType = 'SingleProject' | 'AllProjects'

export interface ProjectSummaryDto {
  id: string
  projectKey: string
  name: string
  workingCopyDisplayPath: string
  uProjectDisplayPath: string
  engineDisplayPath: string
  archiveDisplayPath: string
  gameTarget?: string | null
  clientTarget?: string | null
  serverTarget?: string | null
  allowedBuildConfigurations: string[]
  defaultExtraUatArgs: string[]
  createdAtUtc: string
  updatedAtUtc: string
}

export interface ProjectConfigDto {
  id: string
  projectKey: string
  name: string
  workingCopyPath: string
  uProjectPath: string
  engineRootPath: string
  archiveRootPath: string
  gameTarget?: string | null
  clientTarget?: string | null
  serverTarget?: string | null
  allowedBuildConfigurations: string[]
  defaultExtraUatArgs: string[]
  createdAtUtc: string
  updatedAtUtc: string
}

export interface UpsertProjectRequest {
  projectKey?: string | null
  name: string
  workingCopyPath: string
  uProjectPath: string
  engineRootPath: string
  archiveRootPath: string
  gameTarget?: string | null
  clientTarget?: string | null
  serverTarget?: string | null
  allowedBuildConfigurations: string[]
  defaultExtraUatArgs: string[]
}

export interface ImportProjectConflictDto {
  name: string
  projectKey?: string | null
  reason: string
}

export interface ImportProjectsResult {
  created: number
  updated: number
  conflicts: number
  total: number
  conflictItems: ImportProjectConflictDto[]
}

export interface QueueBuildRequest {
  projectId: string
  revision: string
  targetType: BuildTargetType
  buildConfiguration: string
  clean: boolean
  pak: boolean
  ioStore: boolean
  extraUatArgs: string[]
}

export interface BuildSummaryDto {
  id: string
  projectId: string
  projectName: string
  revision: string
  triggerSource: BuildTriggerSource
  scheduleId?: string | null
  targetType: BuildTargetType
  targetName: string
  buildConfiguration: string
  status: BuildStatus
  currentPhase: BuildPhase
  progressPercent: number
  statusMessage: string
  queuedAtUtc: string
  startedAtUtc?: string | null
  finishedAtUtc?: string | null
  durationSeconds?: number | null
  errorSummary?: string | null
  downloadUrl?: string | null
}

export interface BuildDetailDto extends BuildSummaryDto {
  clean: boolean
  pak: boolean
  ioStore: boolean
  extraUatArgs: string[]
  exitCode?: number | null
  logLineCount: number
  svnCommandPreview?: string | null
  uatCommandPreview?: string | null
}

export interface BuildLogSnapshotDto {
  lines: string[]
  includedLines: number
  totalLines: number
  truncated: boolean
}

export interface UpsertBuildScheduleRequest {
  name: string
  enabled: boolean
  scopeType: BuildScheduleScopeType
  projectId?: string | null
  timeOfDayLocal: string
  targetType: BuildTargetType
  buildConfiguration: string
  clean: boolean
  pak: boolean
  ioStore: boolean
  extraUatArgs: string[]
}

export interface BuildScheduleSummaryDto {
  id: string
  name: string
  enabled: boolean
  scopeType: BuildScheduleScopeType
  projectId?: string | null
  projectName?: string | null
  timeOfDayLocal: string
  targetType: BuildTargetType
  buildConfiguration: string
  clean: boolean
  pak: boolean
  ioStore: boolean
  lastTriggeredAtUtc?: string | null
  lastTriggeredBuildCount: number
  lastTriggerMessage?: string | null
  createdAtUtc: string
  updatedAtUtc: string
}

export interface BuildScheduleDetailDto extends BuildScheduleSummaryDto {
  extraUatArgs: string[]
  lastTriggeredLocalDate?: string | null
}

export interface BuildScheduleRunResultDto {
  scheduleId: string
  requestedCount: number
  enqueuedCount: number
  failedCount: number
  message: string
}

export interface ValidationProblemDetails {
  errors?: Record<string, string[]>
  title?: string
  message?: string
}

export interface BuildEventEnvelope {
  eventType: 'build-status' | 'build-progress' | 'build-log' | 'build-finished' | 'heartbeat'
  buildId: string
  payload: Record<string, unknown>
  occurredAtUtc: string
}
