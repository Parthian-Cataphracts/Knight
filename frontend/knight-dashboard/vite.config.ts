import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { fileURLToPath, URL } from "node:url";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },
  server: { port: 5173 },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],

    // Pinned, because the suite stubs fetch and the client only calls fetch when
    // the mocks are off. A developer with VITE_USE_MOCKS=true in .env.local
    // would otherwise watch seven screen tests fail for a reason that has
    // nothing to do with anything they changed.
    env: { VITE_USE_MOCKS: "false" },
  },
});
