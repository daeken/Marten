import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In dev, set "devOrigin": "http://localhost:5173" in ~/.marten/config.json so the API accepts the dev server.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: { '/api': { target: 'http://127.0.0.1:7450', ws: true, changeOrigin: true } },
  },
});
