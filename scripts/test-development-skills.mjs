import assert from 'node:assert/strict'
import { readFile, readdir, stat } from 'node:fs/promises'
import path from 'node:path'
import test from 'node:test'

const applicationRoot = path.resolve(process.argv[2] ?? 'templates/trykatch')
const namespace = process.argv[3] ?? 'Trykatch'
const skillNames = ['trykatch-spec', 'trykatch-build-module', 'trykatch-extend-module', 'trykatch-review', 'trykatch-verify']
const skillPaths = skillNames.map((name) => `.agents/skills/${name}/SKILL.md`)
const guidePaths = ['backend', 'frontend', 'security', 'specifications', 'verification']
  .map((name) => `docs/development/${name}.md`)
const instructionPaths = ['AGENTS.md', 'docs/ai-assisted-development.md', ...skillPaths, ...guidePaths]

test('all five skills ship as discoverable folders with stable names and descriptions', async () => {
  const entries = await readdir(path.join(applicationRoot, '.agents/skills'))
  for (const name of skillNames) {
    assert.ok(entries.includes(name), `Missing packaged skill ${name}`)
    const source = await readFile(path.join(applicationRoot, '.agents/skills', name, 'SKILL.md'), 'utf8')
    const header = source.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n/)
    assert.ok(header, `${name} needs frontmatter`)
    assert.equal(header[1].match(/^name: (.+)$/m)?.[1], name)
    const description = header[1].match(/^description: (.+)$/m)?.[1]
    assert.ok(description?.trim(), `${name} needs a discovery description`)
    assert.ok(source.slice(header[0].length).trim(), `${name} needs instructions`)
  }
})

test('instruction links resolve within the generated application, including skill-to-skill links', async () => {
  for (const relativePath of instructionPaths) {
    const filename = path.join(applicationRoot, relativePath)
    const source = await readFile(filename, 'utf8')
    for (const match of source.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
      const target = match[1].split('#')[0]
      if (!target || /^[a-z]+:/i.test(target)) continue
      const destination = path.resolve(path.dirname(filename), target)
      const relative = path.relative(applicationRoot, destination)
      assert.ok(relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative), `${relativePath} links outside the application: ${target}`)
      assert.ok((await stat(destination)).isFile(), `${relativePath} has a missing reference: ${target}`)
    }
  }
})

test('namespace-specific instructions point to real solution and local tool paths', async () => {
  for (const target of [`${namespace}.slnx`, `tools/${namespace}.ModuleTool`, `tests/${namespace}.ArchitectureTests`, `src/Common/${namespace}.Modules.Abstractions/${namespace}Module.cs`]) {
    // Module contract filenames are renamed by the same template symbol as their namespace.
    assert.ok(await stat(path.join(applicationRoot, target)), `Missing renamed target ${target}`)
  }
  for (const relativePath of instructionPaths) {
    const source = await readFile(path.join(applicationRoot, relativePath), 'utf8')
    if (namespace !== 'Trykatch') {
      assert.ok(!source.includes('Trykatch.'), `${relativePath} retained a template namespace`)
    }
  }
  const verification = await readFile(path.join(applicationRoot, 'docs/development/verification.md'), 'utf8')
  assert.ok(verification.includes(`dotnet build ${namespace}.slnx`))
  assert.ok(verification.includes(`--project tools/${namespace}.ModuleTool`))
})

test('every skill and guide is reachable from the task router', async () => {
  const router = await readFile(path.join(applicationRoot, 'AGENTS.md'), 'utf8')
  for (const target of [...skillPaths, ...guidePaths]) {
    assert.ok(router.includes(`(${target})`), `Router cannot discover ${target}`)
  }
})
