import { z } from 'zod'

// Organization role rules mirror SaveRoleCommand validation. Permissions and
// grant authority are still checked independently by the backend.
export const roleSchema = z.object({
  name: z.string().trim().min(1, 'Role names must contain 1-80 characters.').max(80, 'Role names must contain 1-80 characters.'),
  description: z.string().trim().max(240, 'Role descriptions cannot exceed 240 characters.'),
  permissions: z.array(z.string()),
})
