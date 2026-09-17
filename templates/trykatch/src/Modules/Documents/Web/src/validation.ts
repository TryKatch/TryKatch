import { z } from 'zod'

export const documentSchema = z.object({
  title: z.string().trim().min(1, 'Title is required and cannot exceed 200 characters.').max(200, 'Title is required and cannot exceed 200 characters.'),
  description: z.string().trim().max(2000, 'Description cannot exceed 2,000 characters.'),
  documentType: z.enum(['invoice', 'contract', 'certificate', 'report', 'other'], { error: 'Choose a supported document type.' }),
})
