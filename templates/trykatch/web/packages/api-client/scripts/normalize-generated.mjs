import { readdir, readFile, stat, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const generatedDirectory = fileURLToPath(new URL('../src/generated/', import.meta.url))
const generatedFormFileAlias = 'export type IFormFile = Blob;'

async function normalize(directory) {
  for (const entry of await readdir(directory)) {
    const path = join(directory, entry)
    if ((await stat(path)).isDirectory()) {
      await normalize(path)
      continue
    }

    if (!path.endsWith('.ts')) continue
    const source = await readFile(path, 'utf8')
    let content = source
    if (entry === 'iFormFile.ts') {
      if (!content.includes(generatedFormFileAlias)) {
        throw new Error('Generated IFormFile contract no longer has the expected Blob alias.')
      }
      content = content.replace(generatedFormFileAlias, 'export type IFormFile = File;')
    }

    const normalized = `${content.trimEnd()}\n`
    if (source !== normalized) await writeFile(path, normalized)
  }
}

await normalize(generatedDirectory)
