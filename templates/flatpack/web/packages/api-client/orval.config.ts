import { defineConfig } from 'orval'

export default defineConfig({
  flatpack: {
    input: './openapi/FlatpackApp.Api.json',
    output: {
      target: './src/generated/flatpack.ts',
      schemas: './src/generated/models',
      client: 'react-query',
      httpClient: 'fetch',
      mode: 'split',
      clean: true,
      override: { mutator: { path: './src/http.ts', name: 'customFetch' } },
    },
  },
})
