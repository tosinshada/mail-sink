import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// The .NET host serves the built SPA from wwwroot. In dev, Vite proxies the API and hub to it.
const target = process.env.MAILSINK_URL ?? "http://127.0.0.1:8025";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  base: "./",
  build: {
    outDir: "../Mailsink.Server/wwwroot",
    emptyOutDir: true,
    chunkSizeWarningLimit: 900,
  },
  server: {
    port: 5173,
    proxy: {
      "/api": { target, changeOrigin: true },
      "/hubs": { target, changeOrigin: true, ws: true },
    },
  },
});
