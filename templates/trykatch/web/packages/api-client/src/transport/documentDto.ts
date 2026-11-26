export interface DocumentDto {
  id: string
  title: string
  description: string
  fileName: string
  createdAt: string
  documentType: string
  metadata: {
    updatedAt: string | null
    mediaType: string
    sizeBytes: number | string
    sha256: string
  }
  lifecycle: {
    status: string
    archivedAt: string | null
    archivedBy: string | null
    deletedAt: string | null
    deletedBy: string | null
    deletionReason: string | null
  }
}
