import { readdir, readFile, stat, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const generatedDirectory = fileURLToPath(new URL('../src/generated/', import.meta.url))

async function normalize(directory) {
  for (const entry of await readdir(directory)) {
    const path = join(directory, entry)
    if ((await stat(path)).isDirectory()) {
      await normalize(path)
      continue
    }

    if (!path.endsWith('.ts')) continue
    const source = await readFile(path, 'utf8')
    const normalized = `${source.trimEnd()}\n`
    if (source !== normalized) await writeFile(path, normalized)
  }
}

await normalize(generatedDirectory)
