import type { PropsWithChildren } from 'react'
import { NavLink } from 'react-router-dom'

export function Layout({ children }: PropsWithChildren) {
  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">Unreal Windows Packaging</p>
          <h1>本地打包控制台</h1>
        </div>
        <nav className="topnav">
          <NavLink to="/builds" className={({ isActive }) => (isActive ? 'active' : undefined)}>
            构建中心
          </NavLink>
          <NavLink to="/schedules" className={({ isActive }) => (isActive ? 'active' : undefined)}>
            定时任务
          </NavLink>
          <NavLink to="/projects" className={({ isActive }) => (isActive ? 'active' : undefined)}>
            项目配置
          </NavLink>
        </nav>
      </header>
      <main className="page-shell">{children}</main>
    </div>
  )
}
