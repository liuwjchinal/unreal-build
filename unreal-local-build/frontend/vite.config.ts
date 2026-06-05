import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const defaultFrontendPort = 5173
const defaultBackendOrigin = 'http://localhost:5080'

export default defineConfig(({ command }) => {
  const frontendPort = Number(process.env.LOCAL_BUILD_FRONTEND_PORT ?? defaultFrontendPort)
  const backendOrigin = (process.env.LOCAL_BUILD_BACKEND_ORIGIN ?? defaultBackendOrigin).replace(/\/$/, '')
  const injectedBackendOrigin =
    command === 'serve' ? backendOrigin : (process.env.LOCAL_BUILD_BACKEND_ORIGIN ?? '').replace(/\/$/, '')

  return {
    plugins: [react()],
    define: {
      __LOCAL_BUILD_BACKEND_ORIGIN__: JSON.stringify(injectedBackendOrigin),
    },
    server: {
      host: '0.0.0.0',
      port: frontendPort,
      proxy: {
        '/api': backendOrigin,
      },
    },
    build: {
      outDir: '../backend/wwwroot',
      emptyOutDir: true,
      minify: false,
      sourcemap: true,
    },
  }
})
