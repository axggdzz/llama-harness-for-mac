import { cp, mkdir, rm } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const uiDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const distDir = resolve(uiDir, "dist");

await rm(distDir, { recursive: true, force: true });
await mkdir(distDir, { recursive: true });
for (const file of ["index.html", "styles.css", "app.js"]) {
  await cp(resolve(uiDir, file), resolve(distDir, file));
}
console.log(`Copied UI assets to ${distDir}`);
