import { Navigate, Route, Routes } from 'react-router-dom'
import { Layout } from './components/Layout'
import { BuildsPage } from './pages/BuildsPage'
import { ProjectsPage } from './pages/ProjectsPage'
import { BuildDetailPage } from './pages/BuildDetailPage'

export function App() {
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<Navigate to="/builds" replace />} />
        <Route path="/projects" element={<ProjectsPage />} />
        <Route path="/builds" element={<BuildsPage />} />
        <Route path="/builds/:buildId" element={<BuildDetailPage />} />
      </Routes>
    </Layout>
  )
}
