/** Product branding is independent of .NET namespaces. Override at Vite build time. */
export const applicationName = import.meta.env.VITE_APPLICATION_NAME?.trim() || "Trykatch Product"
