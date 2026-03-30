import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import { formatUtc, joinList, parseTextAreaList } from '../components/formatters'
import type {
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
  allowedBuildConfigurations: string
  defaultExtraUatArgs: string
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
  allowedBuildConfigurations: 'Development\nShipping',
  defaultExtraUatArgs: '',
}

export function ProjectsPage() {
  const [projects, setProjects] = useState<ProjectSummaryDto[]>([])
  const [form, setForm] = useState<ProjectFormState>(EMPTY_FORM)
  const [editingProject, setEditingProject] = useState<ProjectConfigDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [submitting, setSubmitting] = useState(false)
  const [loadingConfigId, setLoadingConfigId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [importConflicts, setImportConflicts] = useState<ImportProjectConflictDto[]>([])
  const fileInputRef = useRef<HTMLInputElement | null>(null)

  useEffect(() => {
    void loadProjects()
  }, [])

  async function loadProjects() {
    setLoading(true)
    setError(null)
    try {
      setProjects(await api.getProjects())
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setLoading(false)
    }
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
      setError((err as Error).message)
    } finally {
      setLoadingConfigId(null)
    }
  }

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)
    setNotice(null)
    setImportConflicts([])

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
      setError((err as Error).message)
    } finally {
      setSubmitting(false)
    }
  }

  async function handleDelete(id: string) {
    if (!window.confirm('确定删除该项目配置吗？')) {
      return
    }

    try {
      await api.deleteProject(id)
      if (editingProject?.id === id) {
        resetEditor()
      }
      setNotice('项目配置已删除。')
      setImportConflicts([])
      await loadProjects()
    } catch (err) {
      setError((err as Error).message)
    }
  }

  async function handleExport() {
    try {
      const blob = await api.exportProjects()
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `projects-export-${new Date().toISOString().replaceAll(':', '-')}.json`
      anchor.click()
      URL.revokeObjectURL(url)
      setNotice('项目配置已导出。')
      setImportConflicts([])
    } catch (err) {
      setError((err as Error).message)
    }
  }

  async function handleImportChange(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    if (!file) {
      return
    }

    try {
      const text = await file.text()
      const payload = JSON.parse(text) as UpsertProjectRequest[]
      const result = await api.importProjects(payload)
      setNotice(`导入完成：新建 ${result.created} 项，更新 ${result.updated} 项，冲突 ${result.conflicts} 项。`)
      setImportConflicts(result.conflictItems)
      await loadProjects()
    } catch (err) {
      setError(`导入失败: ${(err as Error).message}`)
      setImportConflicts([])
    } finally {
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
            <input value={form.uProjectPath} onChange={(event) => setForm({ ...form, uProjectPath: event.target.value })} required />
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
            <input ref={fileInputRef} type="file" accept="application/json" hidden onChange={handleImportChange} />
            <button type="button" className="secondary-button" onClick={() => fileInputRef.current?.click()}>
              导入 JSON
            </button>
            <button type="button" className="secondary-button" onClick={() => void handleExport()}>
              导出 JSON
            </button>
            <button type="button" className="secondary-button" onClick={() => void loadProjects()} disabled={loading}>
              刷新
            </button>
          </div>
        </div>

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
