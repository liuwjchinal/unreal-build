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

export type BuildPlatform = 'Windows' | 'Android' | 'OpenHarmony'

export type BuildTargetType = 'Game' | 'Client' | 'Server'

export type BuildTriggerSource = 'Manual' | 'Schedule'

export type BuildScheduleScopeType = 'SingleProject' | 'AllProjects'

export type BuildAccelerator = 'None' | 'Uba'

export type AndroidPackagingMode = 'ExternalFilesIoStore' | 'SplitObb' | 'DataInsideApk'

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
  androidEnabled?: boolean | null
  androidTextureFlavor?: string | null
  openHarmonyEnabled?: boolean | null
  defaultBuildAccelerator: BuildAccelerator
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
  androidEnabled: boolean
  androidTextureFlavor: string
  openHarmonyEnabled: boolean
  defaultBuildAccelerator: BuildAccelerator
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
  androidEnabled: boolean
  androidTextureFlavor: string
  openHarmonyEnabled: boolean
  defaultBuildAccelerator: BuildAccelerator
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
  platform: BuildPlatform
  targetType: BuildTargetType
  buildConfiguration: string
  buildAccelerator?: BuildAccelerator | null
  androidPackagingMode?: AndroidPackagingMode | null
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
  platform: BuildPlatform
  targetType: BuildTargetType
  targetName: string
  buildConfiguration: string
  buildAccelerator: BuildAccelerator
  androidPackagingMode: AndroidPackagingMode
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

export interface AndroidPackageArtifactDto {
  projectName: string
  packageName: string
  packagingMode: string
  apkPath: string
  dataRoot: string
  apkSizeBytes: number
  totalDataSizeBytes: number
  fileCount: number
  containerFileCount: number
  looseFileCount: number
  chunkCount: number
  largestChunkSizeBytes: number
  chunks: AndroidPackageArtifactChunkDto[]
  generatedAtUtc: string
  installerDownloadUrl: string
  manifestDownloadUrl: string
}

export interface AndroidPackageArtifactChunkDto {
  chunkId: number
  chunkName: string
  fileCount: number
  totalSizeBytes: number
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
  ubaRemoteEnabled: boolean
  ubaHost?: string | null
  ubaListenHost?: string | null
  ubaPort?: number | null
  ubaAgentMaxIdleSeconds?: number | null
  ubaAgentStoreCapacityGb?: number | null
  ubaMaxWorkers?: number | null
  ubaAgentJoinUrl?: string | null
  ubaAgentManualCommand?: string | null
  ubaHostAutoDetected: boolean
  ubaHostWarning?: string | null
  androidPackage?: AndroidPackageArtifactDto | null
}

export interface UbaAgentConfigDto {
  enabled: boolean
  host: string
  port: number
  maxIdleSeconds: number
  storeCapacityGb: number
  maxWorkers: number
  packageDownloadUrl: string
  protocolExampleUrl: string
  manualCommandExample: string
  hostAutoDetected: boolean
  hostWarning?: string | null
  portPoolSize: number
}

export interface BuildLogSnapshotDto {
  lines: string[]
  includedLines: number
  totalLines: number
  truncated: boolean
}

export type BuildStageLogKind =
  | 'SourceSync'
  | 'Build'
  | 'Cook'
  | 'Stage'
  | 'Package'
  | 'Archive'
  | 'Zip'
  | 'UBT'
  | 'Pak'
  | 'IoStore'

export type BuildStageLogStatus = 'Running' | 'Completed' | 'Failed' | 'Interrupted'

export interface BuildStageArtifactDto {
  artifactKey: string
  label: string
  category: string
  fileName: string
  sizeBytes: number
  downloadUrl: string
}

export interface BuildStageLogSummaryDto {
  stageKey: string
  kind: BuildStageLogKind
  displayName: string
  parentStageKey?: string | null
  order: number
  status: BuildStageLogStatus
  startedAtUtc: string
  finishedAtUtc?: string | null
  logLineCount: number
  logDownloadUrl: string
  inputArtifacts: BuildStageArtifactDto[]
}

export interface BuildStageLogListDto {
  stages: BuildStageLogSummaryDto[]
}

export interface BuildStageLogSnapshotDto {
  stage: BuildStageLogSummaryDto
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
  platform: BuildPlatform
  targetType: BuildTargetType
  buildConfiguration: string
  buildAccelerator?: BuildAccelerator | null
  androidPackagingMode?: AndroidPackagingMode | null
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
  platform: BuildPlatform
  targetType: BuildTargetType
  buildConfiguration: string
  buildAccelerator: BuildAccelerator
  androidPackagingMode: AndroidPackagingMode
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
  eventType: 'build-status' | 'build-progress' | 'build-log' | 'build-stage-state' | 'build-finished' | 'heartbeat'
  buildId: string
  payload: Record<string, unknown>
  occurredAtUtc: string
}
