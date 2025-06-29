import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";
import { Toaster } from "@/components/ui/sonner";
import { ThemeProvider } from "next-themes";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: {
    default: "TryKatch - Professional Software Development Services",
    template: "%s | TryKatch"
  },
  description: "Transform your ideas into powerful digital solutions. We deliver high-quality, scalable software development, UI/UX design, and IT consulting services.",
  keywords: [
    "software development",
    "web development", 
    "mobile app development",
    "UI/UX design",
    "IT consulting",
    "custom software",
    "digital transformation",
    "TryKatch"
  ],
  authors: [{ name: "TryKatch Team" }],
  creator: "TryKatch",
  openGraph: {
    type: "website",
    locale: "en_US",
    url: "https://trykatch.com",
    title: "TryKatch - Professional Software Development Services",
    description: "Transform your ideas into powerful digital solutions. We deliver high-quality, scalable software development services.",
    siteName: "TryKatch",
  },
  twitter: {
    card: "summary_large_image",
    title: "TryKatch - Professional Software Development Services",
    description: "Transform your ideas into powerful digital solutions.",
    creator: "@trykatch",
  },
  robots: {
    index: true,
    follow: true,
    googleBot: {
      index: true,
      follow: true,
      "max-video-preview": -1,
      "max-image-preview": "large",
      "max-snippet": -1,
    },
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body
        className={`${geistSans.variable} ${geistMono.variable} antialiased`}
        suppressHydrationWarning
      >
        <ThemeProvider
          attribute="class"
          defaultTheme="light"
          enableSystem
          disableTransitionOnChange={false}
        >
          {children}
          <Toaster 
            position="top-right"
            expand={true}
            richColors
            closeButton
          />
        </ThemeProvider>
      </body>
    </html>
  );
}
