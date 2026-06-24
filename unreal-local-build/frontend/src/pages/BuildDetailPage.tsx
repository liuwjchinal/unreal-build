import { startTransition, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { api, parseBuildEvent } from '../api/client'
import { BuildStatusBadge } from '../components/BuildStatusBadge'
import { formatAndroidPackagingMode, formatBytes, formatDuration, formatPlatform, formatUtc } from '../components/formatters'
import type {
  BuildDetailDto,
  BuildLogSnapshotDto,
  BuildStageLogListDto,
  BuildStageLogSnapshotDto,
  BuildStageLogSummaryDto,
} from '../types/api'

const MAX_LOG_LINES = 4000
const POLL_INTERVAL_MS = 5000
const STREAM_RECONNECTING_MESSAGE = '实时连接短暂中断，正在自动重连。'
const STREAM_FALLBACK_MESSAGE = '实时连接已断开，已切换为轮询刷新。'

type LogMetaState = {
  includedLines: number
  totalLines: number
  truncated: boolean
}

export function BuildDetailPage() {
  const { buildId } = useParams()
  const navigate = useNavigate()
  const [build, setBuild] = useState<BuildDetailDto | null>(null)
  const [logLines, setLogLines] = useState<string[]>([])
  const [logMeta, setLogMeta] = useState<LogMetaState>({ includedLines: 0, totalLines: 0, truncated: false })
  const [stages, setStages] = useState<BuildStageLogSummaryDto[]>([])
  const [selectedStageKey, setSelectedStageKey] = useState<string | null>(null)
  const [stageSnapshot, setStageSnapshot] = useState<BuildStageLogSnapshotDto | null>(null)
  const [stageLoading, setStageLoading] = useState(false)
  const [stageSearch, setStageSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [canceling, setCanceling] = useState(false)
  const [pageError, setPageError] = useState<string | null>(null)
  const [streamWarning, setStreamWarning] = useState<string | null>(null)
  const [copyNotice, setCopyNotice] = useState<string | null>(null)
  const streamWarningRef = useRef<string | null>(null)
  const selectedStageKeyRef = useRef<string | null>(null)

  useEffect(() => {
    streamWarningRef.current = streamWarning
  }, [streamWarning])

  useEffect(() => {
    selectedStageKeyRef.current = selectedStageKey
  }, [selectedStageKey])

  useEffect(() => {
    if (!buildId) {
      setLoading(false)
      setPageError('缺少构建 ID。')
      return
    }

    let disposed = false
    let eventSource: EventSource | null = null
    let pollTimer: number | null = null

    const applyBuildSnapshot = (nextBuild: BuildDetailDto) => {
      setBuild(nextBuild)
    }

    const applyLogSnapshot = (snapshot: BuildLogSnapshotDto) => {
      setLogLines(snapshot.lines)
      setLogMeta({
        includedLines: snapshot.includedLines,
        totalLines: snapshot.totalLines,
        truncated: snapshot.truncated,
      })
      setBuild((current) => (current ? { ...current, logLineCount: snapshot.totalLines } : current))
    }

    const applyStageList = (response: BuildStageLogListDto) => {
      setStages(response.stages)
      const selectedKey = selectedStageKeyRef.current
      const hasSelected = selectedKey && response.stages.some((stage) => stage.stageKey === selectedKey)
      if (!hasSelected) {
        setSelectedStageKey(response.stages[0]?.stageKey ?? null)
      }
    }

    const loadStageSnapshot = async (stageKey: string) => {
      setStageLoading(true)
      try {
        const snapshot = await api.getBuildStageLog(buildId, stageKey, MAX_LOG_LINES)
        if (!disposed) {
          setStageSnapshot(snapshot)
        }
      } catch {
        if (!disposed) {
          setStageSnapshot(null)
        }
      } finally {
        if (!disposed) {
          setStageLoading(false)
        }
      }
    }

    const loadStageList = async () => {
      try {
        const response = await api.getBuildStageLogs(buildId)
        if (!disposed) {
          applyStageList(response)
        }
      } catch {
        if (!disposed) {
          setStages([])
          setSelectedStageKey(null)
          setStageSnapshot(null)
        }
      }
    }

    const isActiveBuild = (value: BuildDetailDto | null) => value?.status === 'Queued' || value?.status === 'Running'

    const load = async () => {
      setLoading(true)
      setPageError(null)
      setStreamWarning(null)

      try {
        const [buildResponse, logResponse] = await Promise.all([api.getBuild(buildId), api.getBuildLog(buildId)])
        if (disposed) {
          return
        }

        applyBuildSnapshot(buildResponse)
        applyLogSnapshot(logResponse)
        await loadStageList()

        eventSource = api.createBuildEventSource(buildId)

        const handleBuildEvent = (message: MessageEvent<string>) => {
          if (disposed) {
            return
          }

          const envelope = parseBuildEvent(message)
          setStreamWarning(null)

          if (envelope.eventType === 'heartbeat') {
            return
          }

          if (envelope.eventType === 'build-log') {
            const lines = Array.isArray(envelope.payload.lines) ? envelope.payload.lines.map(String) : []
            if (lines.length === 0) {
              return
            }

            startTransition(() => {
              setLogLines((current) => {
                const next = [...current, ...lines]
                return next.length > MAX_LOG_LINES ? next.slice(-MAX_LOG_LINES) : next
              })

              setLogMeta((current) => {
                const nextIncluded = Math.min(MAX_LOG_LINES, current.includedLines + lines.length)
                const nextTotal = Number(envelope.payload.totalLines ?? current.totalLines)
                return {
                  includedLines: nextIncluded,
                  totalLines: nextTotal,
                  truncated: current.truncated || current.includedLines + lines.length > MAX_LOG_LINES,
                }
              })

              setBuild((current) => {
                if (!current) {
                  return current
                }

                const nextTotal = Number(envelope.payload.totalLines ?? current.logLineCount)
                return {
                  ...current,
                  logLineCount: nextTotal,
                }
              })
            })

            return
          }

          if (envelope.eventType === 'build-stage-state') {
            const rawStages = Array.isArray(envelope.payload.stages) ? envelope.payload.stages : []
            startTransition(() => {
              applyStageList({ stages: rawStages as BuildStageLogSummaryDto[] })
            })

            const currentStageKey = selectedStageKeyRef.current
            if (currentStageKey) {
              void loadStageSnapshot(currentStageKey)
            }
            return
          }

          if (envelope.eventType === 'build-finished') {
            void api.getBuild(buildId).then((latestBuild) => {
              if (!disposed) {
                applyBuildSnapshot(latestBuild)
              }
            }).catch(() => undefined)
          }

          setBuild((current) => {
            if (!current) {
              return current
            }

            const rawStatus = envelope.payload.status
            const nextStatus = typeof rawStatus === 'string' ? (rawStatus as BuildDetailDto['status']) : current.status
            return {
              ...current,
              status: nextStatus,
              currentPhase: (envelope.payload.phase as BuildDetailDto['currentPhase']) ?? current.currentPhase,
              progressPercent: Number(envelope.payload.progressPercent ?? current.progressPercent),
              statusMessage: String(envelope.payload.statusMessage ?? current.statusMessage),
              errorSummary: (envelope.payload.errorSummary as string | null | undefined) ?? current.errorSummary,
              exitCode: envelope.payload.exitCode == null ? current.exitCode : Number(envelope.payload.exitCode),
              downloadUrl: (envelope.payload.downloadUrl as string | null | undefined) ?? current.downloadUrl,
              startedAtUtc: (envelope.payload.startedAtUtc as string | null | undefined) ?? current.startedAtUtc,
              finishedAtUtc: (envelope.payload.finishedAtUtc as string | null | undefined) ?? current.finishedAtUtc,
            }
          })
        }

        eventSource.onopen = () => {
          if (!disposed) {
            setStreamWarning(null)
          }
        }

        eventSource.onerror = () => {
          if (disposed || !eventSource) {
            return
          }

          if (eventSource.readyState === EventSource.CLOSED) {
            setStreamWarning(STREAM_FALLBACK_MESSAGE)
            return
          }

          setStreamWarning(STREAM_RECONNECTING_MESSAGE)
        }

        eventSource.addEventListener('build-status', handleBuildEvent as EventListener)
        eventSource.addEventListener('build-progress', handleBuildEvent as EventListener)
        eventSource.addEventListener('build-log', handleBuildEvent as EventListener)
        eventSource.addEventListener('build-stage-state', handleBuildEvent as EventListener)
        eventSource.addEventListener('build-finished', handleBuildEvent as EventListener)

        pollTimer = window.setInterval(async () => {
          try {
            const latestBuild = await api.getBuild(buildId)
            if (disposed) {
              return
            }

            startTransition(() => {
              applyBuildSnapshot(latestBuild)
            })

            const shouldSyncLog =
              !eventSource || eventSource.readyState !== EventSource.OPEN || streamWarningRef.current !== null

            if (isActiveBuild(latestBuild) && shouldSyncLog) {
              const [latestLog, latestStages] = await Promise.all([
                api.getBuildLog(buildId, MAX_LOG_LINES),
                api.getBuildStageLogs(buildId).catch(() => ({ stages: [] as BuildStageLogSummaryDto[] })),
              ])

              if (disposed) {
                return
              }

              startTransition(() => {
                applyLogSnapshot(latestLog)
                applyStageList(latestStages)
                setStreamWarning((current) => current ?? STREAM_FALLBACK_MESSAGE)
              })

              const currentStageKey = selectedStageKeyRef.current
              if (currentStageKey) {
                void loadStageSnapshot(currentStageKey)
              }
            }
          } catch {
            if (!disposed) {
              setStreamWarning(STREAM_FALLBACK_MESSAGE)
            }
          }
        }, POLL_INTERVAL_MS)
      } catch (err) {
        if (!disposed) {
          setPageError((err as Error).message)
        }
      } finally {
        if (!disposed) {
          setLoading(false)
        }
      }
    }

    void load()

    return () => {
      disposed = true
      if (pollTimer !== null) {
        window.clearInterval(pollTimer)
      }
      eventSource?.close()
    }
  }, [buildId])

  useEffect(() => {
    if (!buildId || !selectedStageKey) {
      setStageSnapshot(null)
      return
    }

    let disposed = false
    setStageLoading(true)

    api
      .getBuildStageLog(buildId, selectedStageKey, MAX_LOG_LINES)
      .then((snapshot) => {
        if (!disposed) {
          setStageSnapshot(snapshot)
        }
      })
      .catch(() => {
        if (!disposed) {
          setStageSnapshot(null)
        }
      })
      .finally(() => {
        if (!disposed) {
          setStageLoading(false)
        }
      })

    return () => {
      disposed = true
    }
  }, [buildId, selectedStageKey])

  async function handleCancel() {
    if (!buildId || !build) {
      return
    }

    if (!window.confirm('确定取消这个构建吗？')) {
      return
    }

    setCanceling(true)
    setPageError(null)
    try {
      const updated = await api.cancelBuild(buildId)
      setBuild(updated)
    } catch (err) {
      setPageError((err as Error).message)
    } finally {
      setCanceling(false)
    }
  }

  async function handleCopyManualCommand(command: string) {
    try {
      await navigator.clipboard.writeText(command)
      setCopyNotice('已复制手动启动命令。')
    } catch {
      setCopyNotice('复制失败，请手动选中命令复制。')
    }
  }

  const logContent = useMemo(() => logLines.join('\n'), [logLines])
  const selectedStage = useMemo(
    () => stages.find((stage) => stage.stageKey === selectedStageKey) ?? null,
    [selectedStageKey, stages],
  )
  const stageLogLines = stageSnapshot?.lines ?? []
  const filteredStageLines = useMemo(() => {
    const query = stageSearch.trim().toLowerCase()
    if (!query) {
      return stageLogLines
    }

    return stageLogLines.filter((line) => line.toLowerCase().includes(query))
  }, [stageLogLines, stageSearch])
  const stageLogContent = useMemo(() => filteredStageLines.join('\n'), [filteredStageLines])

  if (loading) {
    return <p className="muted-text">正在加载构建详情...</p>
  }

  if (pageError && !build) {
    return (
      <section className="panel">
        <div className="section-title">
          <div>
            <p className="eyebrow">Build Detail</p>
            <h2>构建详情加载失败</h2>
          </div>
        </div>
        <p className="error-text">{pageError}</p>
        <div className="form-actions">
          <button type="button" className="secondary-button" onClick={() => navigate('/builds')}>
            返回构建列表
          </button>
        </div>
      </section>
    )
  }

  if (!build) {
    return <p className="error-text">未找到该构建。</p>
  }

  const canCancel = build.status === 'Queued' || build.status === 'Running'
  const canDownloadStageArchive = build.status !== 'Queued' && build.status !== 'Running' && stages.length > 0
  const androidPackage = build.androidPackage
  const ubaManualCommand =
    build.ubaAgentManualCommand ||
    (build.ubaRemoteEnabled && build.ubaHost && build.ubaPort
      ? `UbaAgent.exe -host=${build.ubaHost}:${build.ubaPort} -dir="%LOCALAPPDATA%\\UnrealLocalBuild\\UbaAgent\\Cache" -capacity=${build.ubaAgentStoreCapacityGb ?? 40} -maxidle=${build.ubaAgentMaxIdleSeconds ?? 120} -summary -quiet`
      : '')
  const ubaHostWarning =
    build.ubaHostWarning ||
    (build.ubaHostAutoDetected
      ? `App:UbaPublicHost is not configured. Auto-detected ${build.ubaHost}; set App:UbaPublicHost if remote agents cannot connect.`
      : '')
  const canJoinUbaRemote =
    build.status === 'Running' &&
    build.currentPhase === 'Build' &&
    build.ubaRemoteEnabled &&
    Boolean(build.ubaAgentJoinUrl)
  const ubaAgentPackageUrl = api.getUbaAgentPackageUrl(build.projectId)

  return (
    <div className="detail-layout">
      <section className="panel">
        <div className="section-title">
          <div>
            <p className="eyebrow">Build Detail</p>
            <h2>
              {build.projectName} / {formatPlatform(build.platform)} / {build.targetType} / {build.buildConfiguration}
            </h2>
          </div>
          <div className="card-actions">
            <BuildStatusBadge status={build.status} />
            {canCancel ? (
              <button type="button" className="danger-button" onClick={() => void handleCancel()} disabled={canceling}>
                {canceling ? '正在取消...' : '取消构建'}
              </button>
            ) : null}
          </div>
        </div>

        {pageError ? <p className="error-text">{pageError}</p> : null}
        {streamWarning ? <p className="muted-text">{streamWarning}</p> : null}

        <div className="progress-row">
          <div className="progress-track">
            <div className="progress-fill" style={{ width: `${build.progressPercent}%` }} />
          </div>
          <strong>{build.progressPercent}%</strong>
        </div>

        <dl className="detail-grid">
          <div>
            <dt>当前阶段</dt>
            <dd>{build.currentPhase}</dd>
          </div>
          <div>
            <dt>状态说明</dt>
            <dd>{build.statusMessage}</dd>
          </div>
          <div>
            <dt>Revision</dt>
            <dd>{build.revision}</dd>
          </div>
          <div>
            <dt>平台</dt>
            <dd>{formatPlatform(build.platform)}</dd>
          </div>
          <div>
            <dt>Target</dt>
            <dd>{build.targetName}</dd>
          </div>
          <div>
            <dt>排队时间</dt>
            <dd>{formatUtc(build.queuedAtUtc)}</dd>
          </div>
          <div>
            <dt>开始时间</dt>
            <dd>{formatUtc(build.startedAtUtc)}</dd>
          </div>
          <div>
            <dt>结束时间</dt>
            <dd>{formatUtc(build.finishedAtUtc)}</dd>
          </div>
          <div>
            <dt>耗时</dt>
            <dd>{formatDuration(build.durationSeconds)}</dd>
          </div>
          <div>
            <dt>日志行数</dt>
            <dd>{build.logLineCount}</dd>
          </div>
          <div>
            <dt>退出码</dt>
            <dd>{build.exitCode ?? '-'}</dd>
          </div>
          <div>
            <dt>Clean</dt>
            <dd>{build.clean ? '是' : '否'}</dd>
          </div>
          <div>
            <dt>Pak / IoStore</dt>
            <dd>
              {build.pak ? 'Pak' : 'No Pak'} / {build.ioStore ? 'IoStore' : 'Skip IoStore'}
            </dd>
          </div>
          {build.platform === 'Android' ? (
            <div>
              <dt>Android Packaging</dt>
              <dd>{formatAndroidPackagingMode(build.androidPackagingMode)}</dd>
            </div>
          ) : null}
        </dl>

        <div className="command-blocks">
          <div>
            <h3>SVN 命令预览</h3>
            <pre>{build.svnCommandPreview || '-'}</pre>
          </div>
          <div>
            <h3>UAT 命令预览</h3>
            <pre>{build.uatCommandPreview || '-'}</pre>
          </div>
        </div>

        <div className="uba-agent-panel">
          <div className="section-title">
            <div>
              <p className="eyebrow">UBA Remote Agent</p>
              <h3>远程编译加速</h3>
            </div>
            <span className={`status-pill ${build.ubaRemoteEnabled ? 'running' : 'queued'}`}>
              {build.ubaRemoteEnabled ? '已启用' : '未启用'}
            </span>
          </div>

          {build.ubaRemoteEnabled ? (
            <>
              <p className="muted-text">
                先在辅助机下载并安装协议处理器，再点击“加速构建”。浏览器会打开 uba-agent:// 并启动辅助机本地 UbaAgent.exe。
              </p>
              <dl className="detail-grid uba-agent-grid">
                <div>
                  <dt>Host</dt>
                  <dd>{build.ubaHost ?? '-'}</dd>
                </div>
                <div>
                  <dt>Port</dt>
                  <dd>{build.ubaPort ?? '-'}</dd>
                </div>
                <div>
                  <dt>Listen Host</dt>
                  <dd>{build.ubaListenHost ?? '0.0.0.0'}</dd>
                </div>
                <div>
                  <dt>Agent Cache / Idle</dt>
                  <dd>
                    {build.ubaAgentStoreCapacityGb ?? 40} GB / {build.ubaAgentMaxIdleSeconds ?? 120}s
                  </dd>
                </div>
                <div>
                  <dt>Host Workers</dt>
                  <dd>{build.ubaMaxWorkers ?? 4}</dd>
                </div>
                <div>
                  <dt>Join URL</dt>
                  <dd>{build.ubaAgentJoinUrl ?? '-'}</dd>
                </div>
                <div>
                  <dt>当前可加入</dt>
                  <dd>{canJoinUbaRemote ? '是' : '否，仅 Running + Build 阶段可加入'}</dd>
                </div>
              </dl>
              {ubaHostWarning ? <p className="notice-text">{ubaHostWarning}</p> : null}
              <div className="form-actions uba-agent-actions">
                <a className="secondary-button" href={ubaAgentPackageUrl}>
                  下载 UBA Agent 安装包
                </a>
                {canJoinUbaRemote ? (
                  <a className="primary-button" href={build.ubaAgentJoinUrl ?? undefined}>
                    加速构建
                  </a>
                ) : (
                  <button type="button" className="primary-button" disabled>
                    加速构建
                  </button>
                )}
                <button
                  type="button"
                  className="secondary-button"
                  onClick={() => void handleCopyManualCommand(ubaManualCommand)}
                  disabled={!ubaManualCommand}
                >
                  复制手动启动命令
                </button>
              </div>
              {copyNotice ? <p className="notice-text">{copyNotice}</p> : null}
              <pre className="uba-command">{ubaManualCommand || '-'}</pre>
            </>
          ) : (
            <p className="muted-text">该构建未启用 UBA，不能加入远程 Agent。</p>
          )}
        </div>

        {androidPackage ? (
          <div className="uba-agent-panel">
            <div className="section-title">
              <div>
                <p className="eyebrow">Android External Data</p>
                <h3>ExternalFilesDir Install</h3>
              </div>
              <span className="status-pill succeeded">{formatAndroidPackagingMode(build.androidPackagingMode)}</span>
            </div>
            <dl className="detail-grid">
              <div>
                <dt>Package</dt>
                <dd>{androidPackage.packageName}</dd>
              </div>
              <div>
                <dt>Runtime Project</dt>
                <dd>{androidPackage.projectName}</dd>
              </div>
              <div>
                <dt>APK</dt>
                <dd>{formatBytes(androidPackage.apkSizeBytes)}</dd>
              </div>
              <div>
                <dt>External Data</dt>
                <dd>
                  {formatBytes(androidPackage.totalDataSizeBytes)} / {androidPackage.fileCount} files (
                  {androidPackage.containerFileCount} containers, {androidPackage.looseFileCount} loose)
                </dd>
              </div>
              <div>
                <dt>Chunks</dt>
                <dd>
                  {androidPackage.chunkCount} chunks / largest {formatBytes(androidPackage.largestChunkSizeBytes)}
                </dd>
              </div>
              <div>
                <dt>Data Root</dt>
                <dd>{androidPackage.dataRoot}</dd>
              </div>
              <div>
                <dt>Generated</dt>
                <dd>{formatUtc(androidPackage.generatedAtUtc)}</dd>
              </div>
            </dl>
            {androidPackage.chunks.length > 0 ? (
              <>
                <p className="muted-text">
                  Selective sync example: ./install-android-external-data.ps1 --chunks{' '}
                  {androidPackage.chunks
                    .slice(0, Math.min(2, androidPackage.chunks.length))
                    .map((chunk) => chunk.chunkId)
                    .join(',')}
                </p>
                <div className="artifact-list">
                  {androidPackage.chunks.slice(0, 6).map((chunk) => (
                    <span key={chunk.chunkId} className="artifact-chip">
                      {chunk.chunkName}: {formatBytes(chunk.totalSizeBytes)} / {chunk.fileCount} files
                    </span>
                  ))}
                  {androidPackage.chunks.length > 6 ? (
                    <span className="artifact-chip">+{androidPackage.chunks.length - 6} more</span>
                  ) : null}
                </div>
              </>
            ) : null}
            <div className="form-actions uba-agent-actions">
              <a className="secondary-button" href={api.toDownloadUrl(androidPackage.installerDownloadUrl) ?? undefined}>
                Download install script
              </a>
              <a className="secondary-button" href={api.toDownloadUrl(androidPackage.manifestDownloadUrl) ?? undefined}>
                Download manifest
              </a>
            </div>
          </div>
        ) : null}

        <div className="form-actions">
          {build.downloadUrl ? (
            <a className="primary-button" href={api.toDownloadUrl(build.downloadUrl) ?? undefined}>
              下载构建产物
            </a>
          ) : (
            <span />
          )}
          {canDownloadStageArchive ? (
            <a className="secondary-button" href={api.toDownloadUrl(`/api/builds/${build.id}/stage-logs/download`) ?? undefined}>
              下载全部阶段日志
            </a>
          ) : null}
        </div>

        {build.errorSummary ? (
          <div className="error-panel">
            <h3>错误摘要</h3>
            <pre className="error-block">{build.errorSummary}</pre>
          </div>
        ) : null}
      </section>

      <section className="panel">
        <div className="section-title">
          <div>
            <p className="eyebrow">Live Log</p>
            <h2>实时总日志</h2>
          </div>
          <div className="metrics-inline">
            <span>显示 {logMeta.includedLines}</span>
            <span>总行数 {logMeta.totalLines}</span>
          </div>
        </div>

        {logMeta.truncated ? <p className="muted-text">总日志视图仅保留最近 4000 行，较早内容会自动裁剪。</p> : null}
        <pre className="log-viewer">{logContent || '暂无日志输出。'}</pre>
      </section>

      <section className="panel stage-log-panel">
        <div className="section-title">
          <div>
            <p className="eyebrow">Stage Logs</p>
            <h2>阶段日志</h2>
          </div>
          <div className="metrics-inline">
            <span>阶段数 {stages.length}</span>
            <span>当前 {selectedStage?.displayName ?? '-'}</span>
          </div>
        </div>

        <div className="stage-log-layout">
          <div className="stage-log-sidebar">
            {stages.length === 0 ? (
              <p className="muted-text">当前构建尚未生成阶段日志。</p>
            ) : (
              stages.map((stage) => (
                <button
                  key={stage.stageKey}
                  type="button"
                  className={`stage-item${selectedStageKey === stage.stageKey ? ' active' : ''}${stage.parentStageKey ? ' child' : ''}`}
                  onClick={() => setSelectedStageKey(stage.stageKey)}
                >
                  <span>{stage.displayName}</span>
                  <small>
                    {stage.status} / {stage.logLineCount} 行
                  </small>
                </button>
              ))
            )}
          </div>

          <div className="stage-log-content">
            {selectedStage ? (
              <>
                <div className="stage-log-toolbar">
                  <div>
                    <h3>{selectedStage.displayName}</h3>
                    <p className="muted-text">
                      {selectedStage.status} / {formatUtc(selectedStage.startedAtUtc)} - {formatUtc(selectedStage.finishedAtUtc)}
                    </p>
                  </div>
                  <div className="card-actions">
                    <a className="secondary-button" href={api.toDownloadUrl(selectedStage.logDownloadUrl) ?? undefined}>
                      下载阶段日志
                    </a>
                  </div>
                </div>

                <label className="stage-search-label">
                  <span>阶段日志查找</span>
                  <input
                    value={stageSearch}
                    onChange={(event) => setStageSearch(event.target.value)}
                    placeholder="输入关键字过滤当前阶段已加载日志"
                  />
                </label>

                <div className="metrics-inline">
                  <span>显示 {stageSnapshot?.includedLines ?? 0}</span>
                  <span>总行数 {stageSnapshot?.totalLines ?? selectedStage.logLineCount}</span>
                  {stageSearch ? <span>匹配 {filteredStageLines.length}</span> : null}
                </div>

                {selectedStage.inputArtifacts.length > 0 ? (
                  <div className="artifact-list">
                    {selectedStage.inputArtifacts.map((artifact) => (
                      <a
                        key={artifact.artifactKey}
                        className="artifact-chip"
                        href={api.toDownloadUrl(artifact.downloadUrl) ?? undefined}
                      >
                        {artifact.label}
                      </a>
                    ))}
                  </div>
                ) : (
                  <p className="muted-text">该阶段没有归档输入文件。</p>
                )}

                {stageSnapshot?.truncated ? (
                  <p className="muted-text">阶段日志视图仅保留最近 4000 行，较早内容会自动裁剪。</p>
                ) : null}

                <pre className="log-viewer">
                  {stageLoading ? '正在加载阶段日志...' : stageLogContent || '当前阶段暂无日志输出。'}
                </pre>
              </>
            ) : (
              <p className="muted-text">选择一个阶段以查看独立日志和输入归档。</p>
            )}
          </div>
        </div>
      </section>
    </div>
  )
}
