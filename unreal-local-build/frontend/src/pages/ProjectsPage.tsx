import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { formatUtc, joinList, parseTextAreaList } from '../components/formatters'
import type {
  BuildAccelerator,
  ImportProjectConflictDto,
  ProjectConfigDto,
  ProjectSummaryDto,
  UpsertProjectRequest,
} from '../types/api'

interface ProjectFormState {
  name: string
  workingCopyPath: string
  uProjectPath: string
  engineRootPath: string
  archiveRootPath: string
  gameTarget: string
  clientTarget: string
  serverTarget: string
  androidEnabled: boolean
  androidTextureFlavor: string
  openHarmonyEnabled: boolean
  defaultBuildAccelerator: BuildAccelerator
  allowedBuildConfigurations: string
  defaultExtraUatArgs: string
}

interface ImportedProjectFileItem {
  projectKey?: unknown
  ProjectKey?: unknown
  name?: unknown
  Name?: unknown
  workingCopyPath?: unknown
  WorkingCopyPath?: unknown
  uProjectPath?: unknown
  UProjectPath?: unknown
  engineRootPath?: unknown
  EngineRootPath?: unknown
  archiveRootPath?: unknown
  ArchiveRootPath?: unknown
  gameTarget?: unknown
  GameTarget?: unknown
  clientTarget?: unknown
  ClientTarget?: unknown
  serverTarget?: unknown
  ServerTarget?: unknown
  androidEnabled?: unknown
  AndroidEnabled?: unknown
  androidTextureFlavor?: unknown
  AndroidTextureFlavor?: unknown
  openHarmonyEnabled?: unknown
  OpenHarmonyEnabled?: unknown
  defaultBuildAccelerator?: unknown
  DefaultBuildAccelerator?: unknown
  allowedBuildConfigurations?: unknown
  AllowedBuildConfigurations?: unknown
  defaultExtraUatArgs?: unknown
  DefaultExtraUatArgs?: unknown
}

const EMPTY_FORM: ProjectFormState = {
  name: '',
  workingCopyPath: '',
  uProjectPath: '',
  engineRootPath: '',
  archiveRootPath: '',
  gameTarget: '',
  clientTarget: '',
  serverTarget: '',
  androidEnabled: true,
  androidTextureFlavor: 'ASTC',
  openHarmonyEnabled: false,
  defaultBuildAccelerator: 'None',
  allowedBuildConfigurations: 'Development\nShipping',
  defaultExtraUatArgs: '',
}

function getErrorMessage(error: unknown) {
  return error instanceof Error ? error.message : '未知错误'
}

function readOptionalImportString(value: unknown) {
  if (typeof value !== 'string') {
    return null
  }

  const normalized = value.trim()
  return normalized.length > 0 ? normalized : null
}

function readRequiredImportString(value: unknown, index: number, fieldName: string) {
  const normalized = readOptionalImportString(value)
  if (normalized) {
    return normalized
  }

  throw new Error(`第 ${index + 1} 条项目配置缺少${fieldName}`)
}

function readImportBoolean(value: unknown, fallback: boolean) {
  return typeof value === 'boolean' ? value : fallback
}

function readImportStringList(value: unknown, fallback: string[]) {
  if (!Array.isArray(value)) {
    return [...fallback]
  }

  const normalized = value
    .filter((item): item is string => typeof item === 'string')
    .map((item) => item.trim())
    .filter((item) => item.length > 0)

  return normalized.length > 0 ? normalized : [...fallback]
}

function readBuildAccelerator(value: unknown): BuildAccelerator {
  return readOptionalImportString(value) === 'Uba' ? 'Uba' : 'None'
}

// 兼容旧版导出文件的大写字段，以及后续新增的可选平台开关字段。
function normalizeImportedProject(item: unknown, index: number): UpsertProjectRequest {
  if (!item || typeof item !== 'object' || Array.isArray(item)) {
    throw new Error(`第 ${index + 1} 条项目配置格式不正确`)
  }

  const source = item as ImportedProjectFileItem

  return {
    projectKey: readOptionalImportString(source.projectKey ?? source.ProjectKey),
    name: readRequiredImportString(source.name ?? source.Name, index, '项目名称'),
    workingCopyPath: readRequiredImportString(source.workingCopyPath ?? source.WorkingCopyPath, index, ' SVN 工作副本路径'),
    uProjectPath: readRequiredImportString(source.uProjectPath ?? source.UProjectPath, index, ' .uproject 路径'),
    engineRootPath: readRequiredImportString(source.engineRootPath ?? source.EngineRootPath, index, ' Engine 根目录'),
    archiveRootPath: readRequiredImportString(source.archiveRootPath ?? source.ArchiveRootPath, index, '归档根目录'),
    gameTarget: readOptionalImportString(source.gameTarget ?? source.GameTarget),
    clientTarget: readOptionalImportString(source.clientTarget ?? source.ClientTarget),
    serverTarget: readOptionalImportString(source.serverTarget ?? source.ServerTarget),
    androidEnabled: readImportBoolean(source.androidEnabled ?? source.AndroidEnabled, true),
    androidTextureFlavor: readOptionalImportString(source.androidTextureFlavor ?? source.AndroidTextureFlavor) ?? 'ASTC',
    openHarmonyEnabled: readImportBoolean(source.openHarmonyEnabled ?? source.OpenHarmonyEnabled, false),
    defaultBuildAccelerator: readBuildAccelerator(source.defaultBuildAccelerator ?? source.DefaultBuildAccelerator),
    allowedBuildConfigurations: readImportStringList(
      source.allowedBuildConfigurations ?? source.AllowedBuildConfigurations,
      ['Development', 'Shipping'],
    ),
    defaultExtraUatArgs: readImportStringList(source.defaultExtraUatArgs ?? source.DefaultExtraUatArgs, []),
  }
}

function parseImportFile(text: string) {
  const parsed = JSON.parse(text) as unknown

  if (!Array.isArray(parsed)) {
    throw new Error('导入文件格式不正确，根节点必须是数组')
  }

  if (parsed.length === 0) {
    throw new Error('导入文件为空')
  }

  return parsed.map((item, index) => normalizeImportedProject(item, index))
}

export function ProjectsPage() {
  const [projects, setProjects] = useState<ProjectSummaryDto[]>([])
  const [form, setForm] = useState<ProjectFormState>(EMPTY_FORM)
  const [editingProject, setEditingProject] = useState<ProjectConfigDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [submitting, setSubmitting] = useState(false)
  const [importing, setImporting] = useState(false)
  const [loadingConfigId, setLoadingConfigId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [importError, setImportError] = useState<string | null>(null)
  const [importNotice, setImportNotice] = useState<string | null>(null)
  const [importConflicts, setImportConflicts] = useState<ImportProjectConflictDto[]>([])

  useEffect(() => {
    void loadProjects()
  }, [])

  async function loadProjects() {
    setLoading(true)
    setError(null)

    try {
      setProjects(await api.getProjects())
    } catch (err) {
      setError(getErrorMessage(err))
    } finally {
      setLoading(false)
    }
  }

  function clearImportFeedback() {
    setImportError(null)
    setImportNotice(null)
    setImportConflicts([])
  }

  function resetEditor() {
    setEditingProject(null)
    setForm(EMPTY_FORM)
  }

  function applyConfigToForm(project: ProjectConfigDto) {
    setEditingProject(project)
    setForm({
      name: project.name,
      workingCopyPath: project.workingCopyPath,
      uProjectPath: project.uProjectPath,
      engineRootPath: project.engineRootPath,
      archiveRootPath: project.archiveRootPath,
      gameTarget: project.gameTarget ?? '',
      clientTarget: project.clientTarget ?? '',
      serverTarget: project.serverTarget ?? '',
      androidEnabled: project.androidEnabled,
      androidTextureFlavor: project.androidTextureFlavor || 'ASTC',
      openHarmonyEnabled: project.openHarmonyEnabled,
      defaultBuildAccelerator: project.defaultBuildAccelerator,
      allowedBuildConfigurations: joinList(project.allowedBuildConfigurations),
      defaultExtraUatArgs: joinList(project.defaultExtraUatArgs),
    })
  }

  async function handleEdit(projectId: string) {
    setLoadingConfigId(projectId)
    setError(null)
    setNotice(null)

    try {
      const config = await api.getProjectConfig(projectId)
      applyConfigToForm(config)
    } catch (err) {
      setError(getErrorMessage(err))
    } finally {
      setLoadingConfigId(null)
    }
  }

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)
    setNotice(null)
    clearImportFeedback()

    const payload: UpsertProjectRequest = {
      projectKey: editingProject?.projectKey ?? null,
      name: form.name,
      workingCopyPath: form.workingCopyPath,
      uProjectPath: form.uProjectPath,
      engineRootPath: form.engineRootPath,
      archiveRootPath: form.archiveRootPath,
      gameTarget: form.gameTarget || null,
      clientTarget: form.clientTarget || null,
      serverTarget: form.serverTarget || null,
      androidEnabled: form.androidEnabled,
      androidTextureFlavor: form.androidTextureFlavor,
      openHarmonyEnabled: form.openHarmonyEnabled,
      defaultBuildAccelerator: form.defaultBuildAccelerator,
      allowedBuildConfigurations: parseTextAreaList(form.allowedBuildConfigurations),
      defaultExtraUatArgs: parseTextAreaList(form.defaultExtraUatArgs),
    }

    try {
      if (editingProject) {
        await api.updateProject(editingProject.id, payload)
        setNotice('项目配置已更新。')
      } else {
        await api.createProject(payload)
        setNotice('项目配置已创建。')
      }

      resetEditor()
      await loadProjects()
    } catch (err) {
      setError(getErrorMessage(err))
    } finally {
      setSubmitting(false)
    }
  }

  async function handleDelete(id: string) {
    if (!window.confirm('确定删除这个项目配置吗？')) {
      return
    }

    try {
      await api.deleteProject(id)

      if (editingProject?.id === id) {
        resetEditor()
      }

      clearImportFeedback()
      setNotice('项目配置已删除。')
      await loadProjects()
    } catch (err) {
      setError(getErrorMessage(err))
    }
  }

  async function handleExport() {
    try {
      setError(null)
      clearImportFeedback()

      const blob = await api.exportProjects()
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `projects-export-${new Date().toISOString().replaceAll(':', '-')}.json`
      anchor.click()
      URL.revokeObjectURL(url)

      setImportNotice('项目配置 JSON 已导出。')
    } catch (err) {
      setImportError(`导出失败：${getErrorMessage(err)}`)
    }
  }

  async function handleImportChange(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    if (!file) {
      return
    }

    setImporting(true)
    setError(null)
    setNotice(null)
    clearImportFeedback()

    try {
      const text = await file.text()
      const payload = parseImportFile(text)
      const result = await api.importProjects(payload)

      setImportNotice(`导入完成：新建 ${result.created} 项，更新 ${result.updated} 项，冲突 ${result.conflicts} 项。`)
      setImportConflicts(result.conflictItems)
      await loadProjects()
    } catch (err) {
      setImportError(`导入失败：${getErrorMessage(err)}`)
    } finally {
      setImporting(false)
      event.target.value = ''
    }
  }

  return (
    <div className="page-grid">
      <section className="panel panel-form">
        <div className="section-title">
          <div>
            <p className="eyebrow">Project Registry</p>
            <h2>{editingProject ? '编辑项目配置' : '新增项目配置'}</h2>
          </div>
          {editingProject ? (
            <button type="button" className="secondary-button" onClick={resetEditor}>
              取消编辑
            </button>
          ) : null}
        </div>

        <form className="form-grid" onSubmit={handleSubmit}>
          <label>
            项目名称
            <input value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} required />
          </label>
          <label>
            SVN 工作副本
            <input
              value={form.workingCopyPath}
              onChange={(event) => setForm({ ...form, workingCopyPath: event.target.value })}
              required
            />
          </label>
          <label>
            .uproject 路径
            <input
              value={form.uProjectPath}
              onChange={(event) => setForm({ ...form, uProjectPath: event.target.value })}
              required
            />
          </label>
          <label>
            Engine 根目录
            <input
              value={form.engineRootPath}
              onChange={(event) => setForm({ ...form, engineRootPath: event.target.value })}
              required
            />
          </label>
          <label>
            归档根目录
            <input
              value={form.archiveRootPath}
              onChange={(event) => setForm({ ...form, archiveRootPath: event.target.value })}
              required
            />
          </label>
          <label>
            Game Target
            <input value={form.gameTarget} onChange={(event) => setForm({ ...form, gameTarget: event.target.value })} />
          </label>
          <label>
            Client Target
            <input value={form.clientTarget} onChange={(event) => setForm({ ...form, clientTarget: event.target.value })} />
          </label>
          <label>
            Server Target
            <input value={form.serverTarget} onChange={(event) => setForm({ ...form, serverTarget: event.target.value })} />
          </label>
          <label className="checkbox-row">
            <input
              type="checkbox"
              checked={form.androidEnabled}
              onChange={(event) => setForm({ ...form, androidEnabled: event.target.checked })}
            />
            启用 Android 构建
          </label>
          <label>
            Android Texture Flavor
            <select
              value={form.androidTextureFlavor}
              onChange={(event) => setForm({ ...form, androidTextureFlavor: event.target.value })}
              disabled={!form.androidEnabled}
            >
              <option value="ASTC">ASTC</option>
            </select>
          </label>
          <label className="checkbox-row">
            <input
              type="checkbox"
              checked={form.openHarmonyEnabled}
              onChange={(event) => setForm({ ...form, openHarmonyEnabled: event.target.checked })}
            />
            启用 OpenHarmony 构建
          </label>
          <label>
            默认构建加速器
            <select
              value={form.defaultBuildAccelerator}
              onChange={(event) => setForm({ ...form, defaultBuildAccelerator: event.target.value as BuildAccelerator })}
            >
              <option value="None">关闭</option>
              <option value="Uba">UBA</option>
            </select>
          </label>
          <label className="span-two">
            允许的构建配置
            <textarea
              rows={3}
              value={form.allowedBuildConfigurations}
              onChange={(event) => setForm({ ...form, allowedBuildConfigurations: event.target.value })}
            />
          </label>
          <label className="span-two">
            默认额外 UAT 参数
            <textarea
              rows={4}
              value={form.defaultExtraUatArgs}
              onChange={(event) => setForm({ ...form, defaultExtraUatArgs: event.target.value })}
            />
          </label>

          <div className="span-two">
            <p className="muted-text">Android 第一版固定为 ASTC 测试包，只支持 Game Target。</p>
            <p className="muted-text">
              OpenHarmony 第一版不在 Web 中托管 SDK、hvigor、Node、Java 和签名字典，继续复用 UE 项目里的
              OpenHarmonyRuntimeSettings 与宿主机环境。
            </p>
          </div>

          <div className="form-actions span-two">
            <button type="submit" className="primary-button" disabled={submitting}>
              {submitting ? '提交中...' : editingProject ? '保存修改' : '创建项目'}
            </button>
            {error ? <p className="error-text">{error}</p> : null}
            {!error && notice ? <p className="notice-text">{notice}</p> : null}
          </div>
        </form>
      </section>

      <section className="panel">
        <div className="section-title">
          <div>
            <p className="eyebrow">Projects</p>
            <h2>已登记项目</h2>
          </div>
          <div className="card-actions">
            <label className={`secondary-button file-trigger${importing ? ' is-disabled' : ''}`}>
              {importing ? '导入中...' : '导入 JSON'}
              <input
                type="file"
                accept=".json,application/json"
                className="file-trigger-input"
                onChange={(event) => void handleImportChange(event)}
                disabled={importing}
              />
            </label>
            <button type="button" className="secondary-button" onClick={() => void handleExport()}>
              导出 JSON
            </button>
            <button type="button" className="secondary-button" onClick={() => void loadProjects()} disabled={loading}>
              刷新
            </button>
          </div>
        </div>

        {importError ? <p className="error-text panel-feedback">{importError}</p> : null}
        {!importError && importNotice ? <p className="notice-text panel-feedback">{importNotice}</p> : null}

        {loading ? <p className="muted-text">正在加载项目...</p> : null}
        {!loading && projects.length === 0 ? <p className="muted-text">当前还没有项目配置。</p> : null}

        {importConflicts.length > 0 ? (
          <div className="error-panel">
            <h3>导入冲突</h3>
            <div className="import-conflict-list">
              {importConflicts.map((conflict, index) => (
                <article key={`${conflict.projectKey ?? conflict.name}-${index}`} className="import-conflict-item">
                  <strong>{conflict.name}</strong>
                  <p>{conflict.reason}</p>
                </article>
              ))}
            </div>
          </div>
        ) : null}

        <div className="card-list">
          {projects.map((project) => (
            <article className="project-card" key={project.id}>
              <div className="project-card-head">
                <div>
                  <h3>{project.name}</h3>
                  <p className="muted-text">最近更新：{formatUtc(project.updatedAtUtc)}</p>
                </div>
                <div className="card-actions">
                  <button
                    type="button"
                    className="secondary-button"
                    onClick={() => void handleEdit(project.id)}
                    disabled={loadingConfigId === project.id}
                  >
                    {loadingConfigId === project.id ? '加载中...' : '编辑'}
                  </button>
                  <button type="button" className="danger-button" onClick={() => void handleDelete(project.id)}>
                    删除
                  </button>
                </div>
              </div>
              <dl className="detail-grid">
                <div>
                  <dt>工作副本</dt>
                  <dd>{project.workingCopyDisplayPath}</dd>
                </div>
                <div>
                  <dt>uproject</dt>
                  <dd>{project.uProjectDisplayPath}</dd>
                </div>
                <div>
                  <dt>引擎</dt>
                  <dd>{project.engineDisplayPath}</dd>
                </div>
                <div>
                  <dt>归档目录</dt>
                  <dd>{project.archiveDisplayPath}</dd>
                </div>
                <div>
                  <dt>Targets</dt>
                  <dd>
                    Game={project.gameTarget || '-'} / Client={project.clientTarget || '-'} / Server={project.serverTarget || '-'}
                  </dd>
                </div>
                <div>
                  <dt>Android</dt>
                  <dd>{project.androidEnabled ? `已启用 / ${project.androidTextureFlavor}` : '未启用'}</dd>
                </div>
                <div>
                  <dt>OpenHarmony</dt>
                  <dd>{project.openHarmonyEnabled ? '已启用' : '未启用'}</dd>
                </div>
                <div>
                  <dt>默认加速器</dt>
                  <dd>{project.defaultBuildAccelerator}</dd>
                </div>
                <div>
                  <dt>配置</dt>
                  <dd>{project.allowedBuildConfigurations.join(' / ')}</dd>
                </div>
              </dl>
            </article>
          ))}
        </div>
      </section>
    </div>
  )
}
