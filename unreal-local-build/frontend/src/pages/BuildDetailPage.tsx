import { startTransition, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { api, parseBuildEvent } from '../api/client'
import { BuildStatusBadge } from '../components/BuildStatusBadge'
import { formatDuration, formatUtc } from '../components/formatters'
import type { BuildDetailDto, BuildLogSnapshotDto } from '../types/api'

const MAX_LOG_LINES = 4000
const POLL_INTERVAL_MS = 5000
const STREAM_RECONNECTING_MESSAGE = '实时连接短暂中断，正在自动重连...'
const STREAM_FALLBACK_MESSAGE = '实时连接已断开，已切换为轮询刷新。'

export function BuildDetailPage() {
  const { buildId } = useParams()
  const navigate = useNavigate()
  const [build, setBuild] = useState<BuildDetailDto | null>(null)
  const [logLines, setLogLines] = useState<string[]>([])
  const [logMeta, setLogMeta] = useState({ includedLines: 0, totalLines: 0, truncated: false })
  const [loading, setLoading] = useState(true)
  const [canceling, setCanceling] = useState(false)
  const [pageError, setPageError] = useState<string | null>(null)
  const [streamWarning, setStreamWarning] = useState<string | null>(null)
  const streamWarningRef = useRef<string | null>(null)

  useEffect(() => {
    streamWarningRef.current = streamWarning
  }, [streamWarning])

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
              const latestLog = await api.getBuildLog(buildId, MAX_LOG_LINES)
              if (disposed) {
                return
              }

              startTransition(() => {
                applyLogSnapshot(latestLog)
                setStreamWarning((current) => current ?? STREAM_FALLBACK_MESSAGE)
              })
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

  const logContent = useMemo(() => logLines.join('\n'), [logLines])

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

  return (
    <div className="detail-layout">
      <section className="panel">
        <div className="section-title">
          <div>
            <p className="eyebrow">Build Detail</p>
            <h2>
              {build.projectName} / {build.targetType} / {build.buildConfiguration}
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

        {build.downloadUrl ? (
          <div className="form-actions">
            <a className="primary-button" href={api.toDownloadUrl(build.downloadUrl) ?? undefined}>
              下载构建产物
            </a>
          </div>
        ) : null}

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
            <h2>实时日志</h2>
          </div>
          <div className="metrics-inline">
            <span>显示 {logMeta.includedLines}</span>
            <span>总行数 {logMeta.totalLines}</span>
          </div>
        </div>

        {logMeta.truncated ? <p className="muted-text">日志视图仅保留最近 4000 行，较早内容会自动裁剪。</p> : null}
        <pre className="log-viewer">{logContent || '暂无日志输出。'}</pre>
      </section>
    </div>
  )
}
