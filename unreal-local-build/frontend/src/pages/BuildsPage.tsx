import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import { BuildStatusBadge } from '../components/BuildStatusBadge'
import { formatDuration, formatSvnRevision, formatUtc, parseTextAreaList } from '../components/formatters'
import type { BuildSummaryDto, BuildTargetType, ProjectSummaryDto, QueueBuildRequest } from '../types/api'

const DEFAULT_TARGET: BuildTargetType = 'Game'

export function BuildsPage() {
  const navigate = useNavigate()
  const [projects, setProjects] = useState<ProjectSummaryDto[]>([])
  const [builds, setBuilds] = useState<BuildSummaryDto[]>([])
  const [projectId, setProjectId] = useState('')
  const [revision, setRevision] = useState('HEAD')
  const [targetType, setTargetType] = useState<BuildTargetType>(DEFAULT_TARGET)
  const [buildConfiguration, setBuildConfiguration] = useState('Development')
  const [clean, setClean] = useState(false)
  const [pak, setPak] = useState(true)
  const [ioStore, setIoStore] = useState(true)
  const [extraUatArgs, setExtraUatArgs] = useState('')
  const [loading, setLoading] = useState(true)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const selectedProject = useMemo(
    () => projects.find((project) => project.id === projectId) ?? null,
    [projectId, projects],
  )

  useEffect(() => {
    void bootstrap()
  }, [])

  useEffect(() => {
    if (!selectedProject) {
      return
    }

    if (!selectedProject.allowedBuildConfigurations.includes(buildConfiguration)) {
      setBuildConfiguration(selectedProject.allowedBuildConfigurations[0] ?? 'Development')
    }
  }, [buildConfiguration, selectedProject])

  useEffect(() => {
    const timer = window.setInterval(() => {
      void loadBuilds()
    }, 5000)

    return () => window.clearInterval(timer)
  }, [])

  async function bootstrap() {
    setLoading(true)
    setError(null)
    try {
      const [projectItems, buildItems] = await Promise.all([api.getProjects(), api.getBuilds()])
      setProjects(projectItems)
      setBuilds(buildItems)
      if (projectItems.length > 0) {
        setProjectId(projectItems[0].id)
        setBuildConfiguration(projectItems[0].allowedBuildConfigurations[0] ?? 'Development')
      }
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoading(false)
    }
  }

  async function loadBuilds() {
    try {
      setBuilds(await api.getBuilds())
    } catch (err) {
      setError((err as Error).message)
    }
  }

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!projectId) {
      setError('请先选择项目。')
      return
    }

    setSubmitting(true)
    setError(null)

    const payload: QueueBuildRequest = {
      projectId,
      revision,
      targetType,
      buildConfiguration,
      clean,
      pak,
      ioStore,
      extraUatArgs: parseTextAreaList(extraUatArgs),
    }

    try {
      const build = await api.queueBuild(payload)
      await Promise.all([loadBuilds(), api.getBuild(build.id)])
      navigate(`/builds/${build.id}`)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setSubmitting(false)
    }
  }

  const runningCount = builds.filter((build) => build.status === 'Running').length
  const queuedCount = builds.filter((build) => build.status === 'Queued').length

  return (
    <div className="page-grid">
      <section className="panel panel-form">
        <div className="section-title">
          <div>
            <p className="eyebrow">Queue Build</p>
            <h2>发起新构建</h2>
          </div>
          <div className="metrics-inline">
            <span>运行中 {runningCount}</span>
            <span>排队中 {queuedCount}</span>
          </div>
        </div>

        <form className="form-grid" onSubmit={handleSubmit}>
          <label>
            项目
            <select value={projectId} onChange={(event) => setProjectId(event.target.value)} required>
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            Revision
            <input value={revision} onChange={(event) => setRevision(event.target.value)} required />
          </label>
          <label>
            Target 类型
            <select value={targetType} onChange={(event) => setTargetType(event.target.value as BuildTargetType)}>
              <option value="Game">Game</option>
              <option value="Client">Client</option>
              <option value="Server">Server</option>
            </select>
          </label>
          <label>
            构建配置
            <select value={buildConfiguration} onChange={(event) => setBuildConfiguration(event.target.value)}>
              {(selectedProject?.allowedBuildConfigurations ?? ['Development', 'Shipping']).map((config) => (
                <option key={config} value={config}>
                  {config}
                </option>
              ))}
            </select>
          </label>
          <label className="checkbox-row">
            <input type="checkbox" checked={clean} onChange={(event) => setClean(event.target.checked)} />
            Clean
          </label>
          <label className="checkbox-row">
            <input type="checkbox" checked={pak} onChange={(event) => setPak(event.target.checked)} />
            Pak
          </label>
          <label className="checkbox-row">
            <input type="checkbox" checked={ioStore} onChange={(event) => setIoStore(event.target.checked)} />
            IoStore
          </label>
          <label className="span-two">
            额外 UAT 参数
            <textarea rows={4} value={extraUatArgs} onChange={(event) => setExtraUatArgs(event.target.value)} />
          </label>

          <div className="form-actions span-two">
            <button type="submit" className="primary-button" disabled={submitting || loading || projects.length === 0}>
              {submitting ? '正在提交...' : '加入构建队列'}
            </button>
            {error ? <p className="error-text">{error}</p> : null}
          </div>
        </form>
      </section>

      <section className="panel">
        <div className="section-title">
          <div>
            <p className="eyebrow">Build History</p>
            <h2>构建列表</h2>
          </div>
          <button type="button" className="secondary-button" onClick={() => void loadBuilds()}>
            刷新
          </button>
        </div>

        {loading ? <p className="muted-text">正在加载构建列表...</p> : null}
        {!loading && builds.length === 0 ? <p className="muted-text">还没有构建记录。</p> : null}

        <div className="build-list">
          {builds.map((build) => (
            <article className="build-item" key={build.id}>
              <div className="build-item-head">
                <div>
                  <div className="build-title-row">
                    <BuildStatusBadge status={build.status} />
                    <Link to={`/builds/${build.id}`} className="build-link">
                      {build.projectName} / {build.targetType} / {build.buildConfiguration}
                    </Link>
                  </div>
                  <p className="muted-text">
                    {formatSvnRevision(build.revision)} / Target {build.targetName}
                  </p>
                </div>
                <div className="build-meta">
                  <span>{formatUtc(build.queuedAtUtc)}</span>
                  <span>耗时 {formatDuration(build.durationSeconds)}</span>
                </div>
              </div>

              <div className="progress-row">
                <div className="progress-track">
                  <div className="progress-fill" style={{ width: `${build.progressPercent}%` }} />
                </div>
                <strong>{build.progressPercent}%</strong>
              </div>

              <div className="build-footer">
                <span>{build.statusMessage}</span>
                <div className="card-actions">
                  {build.downloadUrl ? (
                    <a className="secondary-button" href={api.toDownloadUrl(build.downloadUrl) ?? undefined}>
                      下载产物
                    </a>
                  ) : null}
                  <Link to={`/builds/${build.id}`} className="secondary-button">
                    查看详情
                  </Link>
                </div>
              </div>

              {build.errorSummary ? <pre className="error-block">{build.errorSummary}</pre> : null}
            </article>
          ))}
        </div>
      </section>
    </div>
  )
}
