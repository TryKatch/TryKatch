import { z } from 'zod'

export const projectSchema = z.object({
  name: z.string().trim().min(1, 'Project name is required.').max(120, 'Project name cannot exceed 120 characters.'),
  description: z.string().trim().max(2000, 'Description cannot exceed 2,000 characters.'),
})
