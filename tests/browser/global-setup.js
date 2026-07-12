import { mkdir, rm } from "node:fs/promises";
import path from "node:path";

export default async function globalSetup() {
  const directory = path.resolve("artifacts/playwright");
  await mkdir(directory, { recursive: true });
  await rm(path.join(directory, "mockapi.json"), { force: true });
}
