import { createApplicationRouter } from './router-core'
import { LoginPage } from './views/LoginPage'

export const router = createApplicationRouter(LoginPage)

declare module '@tanstack/react-router' {
  interface Register { router: typeof router }
}
