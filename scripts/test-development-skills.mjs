import assert from 'node:assert/strict'
import { readFile, readdir, stat } from 'node:fs/promises'
import path from 'node:path'
import test from 'node:test'
import { spawnSync } from 'node:child_process'

const applicationRoot = path.resolve(process.argv[2] ?? 'templates/trykatch')
const namespace = process.argv[3] ?? 'Trykatch'
const skillNames = ['trykatch-spec', 'trykatch-build-module', 'trykatch-extend-module', 'trykatch-review', 'trykatch-verify']
const skillPaths = skillNames.map((name) => `.agents/skills/${name}/SKILL.md`)
const guidePaths = ['backend', 'frontend', 'security', 'specifications', 'verification']
  .map((name) => `docs/development/${name}.md`)
const onboardingPaths = ['docs/developer-onboarding.md', 'docs/developer-onboarding.fr.md']
const firstFeaturePaths = ['docs/first-feature.md', 'docs/first-feature.fr.md']
const backendHttpPaths = ['docs/backend-only-http.md', 'docs/backend-only-http.fr.md']
const instructionPaths = ['AGENTS.md', 'docs/ai-assisted-development.md', ...onboardingPaths, ...firstFeaturePaths, ...backendHttpPaths, ...skillPaths, ...guidePaths]

test('user help runtime and both React entry points ship alongside developer onboarding', async () => {
  const read = relative => readFile(path.join(applicationRoot, relative), 'utf8')
  const controller = await read(`src/API/${namespace}.Api/Controllers/AssistantController.cs`)
  assert.ok(controller.includes('[OrganizationScoped]'), 'Help must retain organization-scoped authorization')
  assert.ok(controller.includes('CookieAntiforgery'), 'Chat requests must retain cookie antiforgery')
  const runtime = await read(`src/Common/${namespace}.Modules.AspNetCore/Assistant/AssistantRuntime.cs`)
  assert.ok(runtime.includes('IChatClient'), 'Help must remain provider-neutral')
  const httpTests = await read(`tests/${namespace}.IntegrationTests/AssistantIntegrationTests.cs`)
  assert.ok(httpTests.includes(`global::${namespace}.Api.Modules.EnabledModules.All`),
    'Generated HTTP tests must not confuse an Email field with the Email.Sample namespace')
  const project = await read(`src/Common/${namespace}.Modules.AspNetCore/${namespace}.Modules.AspNetCore.csproj`)
  assert.ok(project.includes('EmbeddedResource'), 'Approved guides must survive deployment without the source checkout')
  for (const guide of ['architecture', 'projects', 'documents', 'isolation', 'module-authoring', 'providers']) {
    assert.ok(project.includes(`docs/assistant/${guide}.md`))
    assert.ok((await read(`docs/assistant/${guide}.md`)).trim(), `Missing approved guide ${guide}`)
  }
  if (await stat(path.join(applicationRoot, 'web/package.json')).catch(() => null)) {
    const shell = await read('web/apps/web/src/shell/AppShell.tsx')
    assert.match(shell, /className="help-chat-launcher"/, 'The floating AI Help entry point must ship')
    assert.match(shell, /className="account-menu-item"[^\n]*setHelpOpen\(true\)[^\n]*t\('AI Help'\)/,
      'The account-menu AI Help entry point must ship')
    assert.ok(shell.includes('AssistantChatProvider') && shell.includes('<AssistantChat />'))
    assert.ok((await read('web/apps/web/src/features/assistant/AssistantChat.tsx')).includes('conversationToken'))
    const routerEntry = await read('web/apps/web/src/router.tsx')
    const routerCore = await read('web/apps/web/src/router-core.tsx')
    assert.ok(routerEntry.includes('createApplicationRouter'), 'The React entry point must compose the application router')
    assert.ok(routerCore.includes('assistant'), 'The shared application router must retain the AI Help route')
  }
})

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

test('developer onboarding is discoverable and covers the complete development workflow without another AI service', async () => {
  const readme = await readFile(path.join(applicationRoot, 'README.md'), 'utf8')
  const router = await readFile(path.join(applicationRoot, 'AGENTS.md'), 'utf8')
  assert.ok(readme.includes('(docs/developer-onboarding.md)'))
  assert.ok(router.includes('(docs/developer-onboarding.md)'))
  const english = await readFile(path.join(applicationRoot, onboardingPaths[0]), 'utf8')
  for (const expected of ['AGENTS.md', 'module facts', 'module create', '--with-web', 'IntegrationEvents', 'outbox', 'OpenAPI', 'RLS', 'module-overrides.ts', 'verification.md', 'Do not modify files', 'No additional AI provider key']) {
    assert.ok(english.includes(expected), `Onboarding is missing ${expected}`)
  }
  assert.ok(english.includes('(developer-onboarding.fr.md)'))
  const french = await readFile(path.join(applicationRoot, onboardingPaths[1]), 'utf8')
  assert.ok(french.includes('(developer-onboarding.md)'))
  assert.ok(french.includes('AGENTS.md'))
  assert.ok(french.includes('IntegrationEvents'))
  assert.ok(french.includes('OpenAPI'))
  if (!await stat(path.join(applicationRoot, 'web/package.json')).catch(() => null)) {
    assert.ok(english.includes('Backend-only applications skip the frontend steps'))
    assert.ok(french.includes('sans frontend'))
  }
})

test('the template prints onboarding for each UI mode using a non-executing instruction post-action', async () => {
  const configuration = JSON.parse(await readFile(new URL('../templates/trykatch/.template.config/template.json', import.meta.url), 'utf8'))
  const actions = configuration.postActions.filter(action => action.actionId === 'AC1156F7-BB77-4DB8-B28F-24EEBCCA1E5C')
  assert.equal(actions.length, 2)
  for (const mode of ['react', 'none']) {
    const action = actions.find(action => action.condition === `(ui == "${mode}")`)
    assert.ok(action, `Missing ${mode} onboarding message`)
    assert.equal(action.args, undefined, 'Onboarding must not execute a command')
    const text = action.manualInstructions[0].text
    assert.ok(text.includes('docs/developer-onboarding.md'))
    assert.ok(text.includes('docs/first-feature.md'))
    assert.ok(text.includes('AGENTS.md'))
    assert.ok(text.includes('No additional AI provider key'))
    if (mode === 'none') assert.ok(text.includes('skip frontend'))
  }
})

test('first-feature walkthrough has falsifiable startup and module checkpoints in both languages', async () => {
  for (let index = 0; index < firstFeaturePaths.length; index++) {
    const source = await readFile(path.join(applicationRoot, firstFeaturePaths[index]), 'utf8')
    for (const expected of ['doctor', 'setup', 'status', '--api-url', 'AppHost', 'EquipmentItem', 'equipment_items', '--with-web', 'module facts', 'OpenAPI', 'RLS', 'dailyRate', '120', 'generate:check']) {
      assert.ok(source.includes(expected), `${firstFeaturePaths[index]} is missing ${expected}`)
    }
    const onboarding = await readFile(path.join(applicationRoot, onboardingPaths[index]), 'utf8')
    assert.ok(onboarding.includes(`(${path.basename(firstFeaturePaths[index])})`))
  }
  const english = await readFile(path.join(applicationRoot, firstFeaturePaths[0]), 'utf8')
  assert.ok(english.includes('Runtime health: unknown'))
  assert.ok(english.includes('not a rental-management product'))
  assert.ok(english.includes('not proof of a running application'))
  const readme = await readFile(path.join(applicationRoot, 'README.md'), 'utf8')
  assert.ok(readme.includes('(docs/first-feature.md)'))
  assert.ok(english.includes('not automatic business translations'))
  assert.ok(english.includes('checks the assistant contract only'))
})

test('backend-only onboarding provides a concrete cookie, antiforgery and workspace HTTP sequence', async () => {
  const languageCommands = []
  for (let index = 0; index < backendHttpPaths.length; index++) {
    const source = await readFile(path.join(applicationRoot, backendHttpPaths[index]), 'utf8')
    const firstFeature = await readFile(path.join(applicationRoot, firstFeaturePaths[index]), 'utf8')
    assert.ok(firstFeature.includes(`(${path.basename(backendHttpPaths[index])})`))
    for (const expected of ['curl', 'jq', 'mktemp', 'trap', '--cookie', '--cookie-jar', 'X-CSRF-TOKEN',
      '/api/v1/auth/antiforgery', '/api/v1/auth/login', 'tenant@trykatch.net', 'Admin@123',
      '/api/v1/me/organizations', 'demo-workspace', '/api/v1/workspace/select', '/api/v1/workspace/current',
      '/api/v1/equipment_items/', 'expectedVersion', 'dailyRate', '/api/v1/auth/logout']) {
      assert.ok(source.includes(expected), `${backendHttpPaths[index]} is missing ${expected}`)
    }
    const commands = [...source.matchAll(/```bash\n([\s\S]*?)\n```/g)].map(match => match[1]).join('\n')
    languageCommands.push(commands)
    const syntax = spawnSync('bash', ['-n'], { input: commands, encoding: 'utf8' })
    assert.equal(syntax.status, 0, `${backendHttpPaths[index]} has invalid Bash: ${syntax.error ?? syntax.stderr}`)
    assert.ok(!/(?:--insecure|curl\s+-k\b)/.test(commands), 'The walkthrough must not bypass TLS')
    const login = commands.indexOf("http_request '/api/v1/auth/login'")
    const refresh = commands.indexOf('http_csrf_token=$(http_csrf)', login)
    const select = commands.indexOf("http_request '/api/v1/workspace/select'")
    assert.ok(login >= 0 && refresh > login && select > refresh, 'Refresh identity-bound antiforgery after login, before selecting a workspace')
  }
  assert.equal(languageCommands[0], languageCommands[1], 'Both language guides must use the same tested HTTP commands')
  const english = await readFile(path.join(applicationRoot, backendHttpPaths[0]), 'utf8')
  assert.ok(english.includes('No generated API client'))
  assert.ok(english.includes('Development'))
  const readme = await readFile(path.join(applicationRoot, 'README.md'), 'utf8')
  if (!await stat(path.join(applicationRoot, 'web/package.json')).catch(() => null)) {
    assert.ok(readme.includes('(docs/backend-only-http.md)'))
    assert.ok(readme.includes('tenant@trykatch.net'))
  }
})
