import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import test from 'node:test'

const source = readFileSync(new URL('../../templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/BlueprintWebIndex.tsx.tpl', import.meta.url), 'utf8')

for (const handler of ['closeEditor', 'closeAction', 'openAction', 'openCreate', 'openEdit']) {
  test(`actual blueprint ${handler} clears shared failed/pending refresh state`, () => {
    const refresh = { error: new Error('Previous dialog refresh failed'), isPending: true,
      reset() { this.error = undefined; this.isPending = false } }
    const refreshScope = { current: 0 }
    const context = vm.createContext({ refresh, refreshScope, save: { reset() {} }, transition: { reset() {} },
      setEditing() {}, setSelectedAction() {}, setActionRecord() {}, setActionValues() {} })
    const declaration = source.match(new RegExp(`  const ${handler} = [^\\n]+`))[0]
      .replaceAll(': __ENTITY__Dto', '').replaceAll(': string', '')
      .replaceAll('__WEB_FIELD_RESET__', '').replaceAll('__WEB_FIELD_EDIT__', '')
    vm.runInContext(declaration + `\n${handler}({}, 'approve')`, context)
    assert.equal(refresh.error, undefined)
    assert.equal(refresh.isPending, false)
    assert.equal(refreshScope.current, 1, 'Invalidate refreshes started in the previous dialog')
  })
}
