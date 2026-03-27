import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import { formatUtc, joinList, parseTextAreaList } from '../components/formatters'
import type {
  BuildScheduleDetailDto,
  BuildScheduleScopeType,
  BuildScheduleSummaryDto,
  BuildTargetType,
  ProjectSummaryDto,
  UpsertBuildScheduleRequest,
} from '../types/api'

interface ScheduleFormState {
  name: string
  enabled: boolean
  scopeType: BuildScheduleScopeType
  projectId: string
  timeOfDayLocal: string
  targetType: BuildTargetType
  buildConfiguration: string
  clean: boolean
  pak: boolean
  ioStore: boolean
  extraUatArgs: string
}

const EMPTY_FORM: ScheduleFormState = {
  name: '',
  enabled: true,
  scopeType: 'SingleProject',
  projectId: '',
  timeOfDayLocal: '12:00',
  targetType: 'Game',
  buildConfiguration: 'Development',
  clean: false,
  pak: true,
  ioStore: true,
  extraUatArgs: '',
}

export function SchedulesPage() {
  const [projects, setProjects] = useState<ProjectSummaryDto[]>([])
  const [schedules, setSchedules] = useState<BuildScheduleSummaryDto[]>([])
  const [editingSchedule, setEditingSchedule] = useState<BuildScheduleDetailDto | null>(null)
  const [form, setForm] = useState<ScheduleFormState>(EMPTY_FORM)
  const [loading, setLoading] = useState(true)
  const [submitting, setSubmitting] = useState(false)
  const [runningNowId, setRunningNowId] = useState<string | null>(null)
  const [loadingDetailId, setLoadingDetailId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const selectedProject = useMemo(
    () => projects.find((project) => project.id === form.projectId) ?? null,
    [form.projectId, projects],
  )

  const availableBuildConfigurations = useMemo(() => {
    if (form.scopeType === 'SingleProject' && selectedProject) {
      return selectedProject.allowedBuildConfigurations
    }

    return Array.from(new Set(projects.flatMap((project) => project.allowedBuildConfigurations))).concat(
      ['Development', 'Shipping'].filter((item, index, list) => !projects.some((project) => project.allowedBuildConfigurations.includes(item)) && list.indexOf(item) === index),
    )
  }, [form.scopeType, projects, selectedProject])

  useEffect(() => {
    void bootstrap()
  }, [])

  useEffect(() => {
    const timer = window.setInterval(() => {
      void loadSchedules()
    }, 15000)

    return () => window.clearInterval(timer)
  }, [])

  useEffect(() => {
    if (form.scopeType !== 'SingleProject' || !selectedProject) {
      return
    }

    if (!selectedProject.allowedBuildConfigurations.includes(form.buildConfiguration)) {
      setForm((current) => ({
        ...current,
        buildConfiguration: selectedProject.allowedBuildConfigurations[0] ?? 'Development',
      }))
    }
  }, [form.buildConfiguration, form.scopeType, selectedProject])

  async function bootstrap() {
    setLoading(true)
    setError(null)
    try {
      const [projectItems, scheduleItems] = await Promise.all([api.getProjects(), api.getSchedules()])
      setProjects(projectItems)
      setSchedules(scheduleItems)
      if (!form.projectId && projectItems.length > 0) {
        setForm((current) => ({ ...current, projectId: projectItems[0].id }))
      }
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoading(false)
    }
  }

  async function loadSchedules() {
    try {
      setSchedules(await api.getSchedules())
    } catch (err) {
      setError((err as Error).message)
    }
  }

  function resetEditor() {
    setEditingSchedule(null)
    setForm({
      ...EMPTY_FORM,
      projectId: projects[0]?.id ?? '',
    })
  }

  function applyScheduleToForm(schedule: BuildScheduleDetailDto) {
    setEditingSchedule(schedule)
    setForm({
      name: schedule.name,
      enabled: schedule.enabled,
      scopeType: schedule.scopeType,
      projectId: schedule.projectId ?? '',
      timeOfDayLocal: schedule.timeOfDayLocal,
      targetType: schedule.targetType,
      buildConfiguration: schedule.buildConfiguration,
      clean: schedule.clean,
      pak: schedule.pak,
      ioStore: schedule.ioStore,
      extraUatArgs: joinList(schedule.extraUatArgs),
    })
  }

  async function handleEdit(id: string) {
    setLoadingDetailId(id)
    setError(null)
    setNotice(null)
    try {
      const schedule = await api.getSchedule(id)
      applyScheduleToForm(schedule)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoadingDetailId(null)
    }
  }

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)
    setNotice(null)

    const payload: UpsertBuildScheduleRequest = {
      name: form.name,
      enabled: form.enabled,
      scopeType: form.scopeType,
      projectId: form.scopeType === 'SingleProject' ? form.projectId || null : null,
      timeOfDayLocal: form.timeOfDayLocal,
      targetType: form.targetType,
      buildConfiguration: form.buildConfiguration,
      clean: form.clean,
      pak: form.pak,
      ioStore: form.ioStore,
      extraUatArgs: parseTextAreaList(form.extraUatArgs),
    }

    try {
      if (editingSchedule) {
        await api.updateSchedule(editingSchedule.id, payload)
        setNotice('定时任务已更新。')
      } else {
        await api.createSchedule(payload)
        setNotice('定时任务已创建。')
      }

      resetEditor()
      await loadSchedules()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setSubmitting(false)
    }
  }

  async function handleRunNow(id: string) {
    setRunningNowId(id)
    setError(null)
    setNotice(null)
    try {
      const result = await api.runScheduleNow(id)
      setNotice(result.message)
      await loadSchedules()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setRunningNowId(null)
    }
  }

  async function handleToggle(id: string) {
    setError(null)
    setNotice(null)
    try {
      const updated = await api.toggleSchedule(id)
      setSchedules((current) => current.map((item) => (item.id === id ? updated : item)))
      setNotice(updated.enabled ? '定时任务已启用。' : '定时任务已停用。')
      if (editingSchedule?.id === id) {
        const detail = await api.getSchedule(id)
        applyScheduleToForm(detail)
      }
    } catch (err) {
      setError((err as Error).message)
    }
  }

  async function handleDelete(id: string) {
    if (!window.confirm('确定删除这个定时任务吗？')) {
      return
    }

    try {
      await api.deleteSchedule(id)
      if (editingSchedule?.id === id) {
        resetEditor()
      }
      setNotice('定时任务已删除。')
      await loadSchedules()
    } catch (err) {
      setError((err as Error).message)
    }
  }

  return (
    <div className="page-grid">
      <section className="panel panel-form">
        <div className="section-title">
          <div>
            <p className="eyebrow">Build Schedules</p>
            <h2>{editingSchedule ? '编辑定时任务' : '新增定时任务'}</h2>
          </div>
          {editingSchedule ? (
            <button type="button" className="secondary-button" onClick={resetEditor}>
              取消编辑
            </button>
          ) : null}
        </div>

        <form className="form-grid" onSubmit={handleSubmit}>
          <label>
            任务名称
            <input value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} required />
          </label>
          <label>
            每日触发时间
            <input
              type="time"
              value={form.timeOfDayLocal}
              onChange={(event) => setForm({ ...form, timeOfDayLocal: event.target.value })}
              required
            />
          </label>
          <label>
            触发范围
            <select
              value={form.scopeType}
              onChange={(event) =>
                setForm((current) => ({
                  ...current,
                  scopeType: event.target.value as BuildScheduleScopeType,
                  projectId: event.target.value === 'SingleProject' ? current.projectId || projects[0]?.id || '' : '',
                }))
              }
            >
              <option value="SingleProject">单项目</option>
              <option value="AllProjects">所有项目</option>
            </select>
          </label>
          <label>
            项目
            <select
              value={form.projectId}
              disabled={form.scopeType !== 'SingleProject'}
              onChange={(event) => setForm({ ...form, projectId: event.target.value })}
            >
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            Target 类型
            <select value={form.targetType} onChange={(event) => setForm({ ...form, targetType: event.target.value as BuildTargetType })}>
              <option value="Game">Game</option>
              <option value="Client">Client</option>
              <option value="Server">Server</option>
            </select>
          </label>
          <label>
            构建配置
            <select value={form.buildConfiguration} onChange={(event) => setForm({ ...form, buildConfiguration: event.target.value })}>
              {availableBuildConfigurations.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>
          </label>
          <label className="checkbox-row">
            <input type="checkbox" checked={form.enabled} onChange={(event) => setForm({ ...form, enabled: event.target.checked })} />
            启用任务
          </label>
          <label className="checkbox-row">
            <input type="checkbox" checked={form.clean} onChange={(event) => setForm({ ...form, clean: event.target.checked })} />
            Clean
          </label>
          <label className="checkbox-row">
            <input type="checkbox" checked={form.pak} onChange={(event) => setForm({ ...form, pak: event.target.checked })} />
            Pak
          </label>
          <label className="checkbox-row">
            <input type="checkbox" checked={form.ioStore} onChange={(event) => setForm({ ...form, ioStore: event.target.checked })} />
            IoStore
          </label>
          <label className="span-two">
            额外 UAT 参数
            <textarea rows={4} value={form.extraUatArgs} onChange={(event) => setForm({ ...form, extraUatArgs: event.target.value })} />
          </label>
          <div className="span-two">
            <p className="muted-text">
              定时任务按部署机本地时间执行。所有项目任务会在触发时读取当前全部项目，并统一以 HEAD 版本入队。
            </p>
          </div>
          <div className="form-actions span-two">
            <button type="submit" className="primary-button" disabled={submitting || loading}>
              {submitting ? '正在保存...' : editingSchedule ? '保存任务' : '创建任务'}
            </button>
            {error ? <p className="error-text">{error}</p> : null}
            {notice ? <p className="notice-text">{notice}</p> : null}
          </div>
        </form>
      </section>

      <section className="panel">
        <div className="section-title">
          <div>
            <p className="eyebrow">Schedule Registry</p>
            <h2>定时任务列表</h2>
          </div>
          <button type="button" className="secondary-button" onClick={() => void loadSchedules()}>
            刷新
          </button>
        </div>

        {loading ? <p className="muted-text">正在加载定时任务...</p> : null}
        {!loading && schedules.length === 0 ? <p className="muted-text">还没有定时任务。</p> : null}

        <div className="card-list">
          {schedules.map((schedule) => (
            <article className="project-card" key={schedule.id}>
              <div className="project-card-head">
                <div>
                  <h3>{schedule.name}</h3>
                  <p className="muted-text">
                    {schedule.scopeType === 'SingleProject'
                      ? `单项目 / ${schedule.projectName ?? '未绑定项目'}`
                      : '所有项目 / 触发时读取当前项目列表'}
                  </p>
                </div>
                <span className={`status-pill ${schedule.enabled ? 'succeeded' : 'queued'}`}>
                  {schedule.enabled ? '已启用' : '已停用'}
                </span>
              </div>

              <dl className="detail-grid">
                <div>
                  <dt>触发时间</dt>
                  <dd>{schedule.timeOfDayLocal}</dd>
                </div>
                <div>
                  <dt>构建参数</dt>
                  <dd>
                    {schedule.targetType} / {schedule.buildConfiguration}
                  </dd>
                </div>
                <div>
                  <dt>最近触发</dt>
                  <dd>{formatUtc(schedule.lastTriggeredAtUtc)}</dd>
                </div>
                <div>
                  <dt>最近入队数量</dt>
                  <dd>{schedule.lastTriggeredBuildCount}</dd>
                </div>
              </dl>

              <p className="muted-text" style={{ marginTop: '1rem' }}>
                {schedule.lastTriggerMessage || '尚未触发。'}
              </p>

              <div className="card-actions" style={{ marginTop: '1rem', justifyContent: 'flex-end' }}>
                <button type="button" className="secondary-button" onClick={() => void handleEdit(schedule.id)} disabled={loadingDetailId === schedule.id}>
                  {loadingDetailId === schedule.id ? '加载中...' : '编辑'}
                </button>
                <button type="button" className="secondary-button" onClick={() => void handleToggle(schedule.id)}>
                  {schedule.enabled ? '停用' : '启用'}
                </button>
                <button type="button" className="secondary-button" onClick={() => void handleRunNow(schedule.id)} disabled={runningNowId === schedule.id}>
                  {runningNowId === schedule.id ? '触发中...' : '立即执行'}
                </button>
                <button type="button" className="danger-button" onClick={() => void handleDelete(schedule.id)}>
                  删除
                </button>
              </div>
            </article>
          ))}
        </div>
      </section>
    </div>
  )
}
