/**
 * Builds the mounted academic identity harness into a temporary directory.
 *
 * Run from `apps/web` (which is where Playwright runs): `npx vite build --config
 * e2e/academic-identity-mount/vite.harness.config.ts`. The output is served to
 * Chromium by the spec through request interception, so no extra web server and
 * no new dependency is needed - `vite` already arrives with the workspace's
 * `vitest`. The studio's own module graph is used unmodified; only the two
 * context providers it reads, and `next/link`, are aliased to harness stubs.
 */
import path from "node:path";
import { defineConfig } from "vite";

const webRoot = process.cwd();
const harnessRoot = path.resolve(webRoot, "e2e/academic-identity-mount");
const outDir = path.resolve(webRoot, "../../node_modules/.cache/taslim-academic-harness");

export default defineConfig({
  root: harnessRoot,
  base: "./",
  logLevel: "warn",
  resolve: {
    alias: [
      { find: "@/components/AuthProvider", replacement: path.resolve(harnessRoot, "harnessAuth.tsx") },
      { find: "@/components/LocaleProvider", replacement: path.resolve(harnessRoot, "harnessLocale.tsx") },
      { find: "next/link", replacement: path.resolve(harnessRoot, "harnessLink.tsx") },
      { find: "@", replacement: path.resolve(webRoot, "src") },
    ],
  },
  build: {
    outDir,
    emptyOutDir: true,
    cssCodeSplit: false,
    assetsInlineLimit: 0,
    minify: false,
    rollupOptions: {
      input: path.resolve(harnessRoot, "harness.html"),
      output: { inlineDynamicImports: true },
    },
  },
});
