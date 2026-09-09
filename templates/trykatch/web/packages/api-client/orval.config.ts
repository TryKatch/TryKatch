import { defineConfig } from 'orval'

export default defineConfig({
  trykatch: {
    input: './openapi/TrykatchApp.Api.json',
    output: {
      target: './src/generated/trykatch.ts',
      schemas: './src/generated/models',
      client: 'react-query',
      httpClient: 'fetch',
      mode: 'split',
      clean: true,
      override: { mutator: { path: './src/http.ts', name: 'customFetch' } },
    },
  },
})
