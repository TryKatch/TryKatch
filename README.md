# TryKatch - Professional Software Development Services

A modern, responsive website for TryKatch - delivering high-qu## 👥 Team

Our experienced team is dedicated to delivering exceptional software solutions:

- **Sudi David** - CTO (Chief Technology Officer)
- **Cedric Justin** - Tech Lead
- **Jean Claude** - Software Developer scalable software development, UI/UX design, and IT consulting services.

## 🚀 Features

- **Modern Tech Stack**: Built with Next.js 15, React 19, and TypeScript
- **Responsive Design**: Mobile-first approach with Tailwind CSS
- **Dark/Light Mode**: Theme switching capability
- **Component Library**: Built with Radix UI and shadcn/ui
- **Performance Optimized**: Next.js App Router with Turbopack
- **SEO Friendly**: Optimized metadata and structured content

## 🛠️ Tech Stack

- **Framework**: Next.js 15 (App Router)
- **Language**: TypeScript
- **Styling**: Tailwind CSS
- **UI Components**: Radix UI, shadcn/ui
- **Icons**: Lucide React
- **Animations**: Framer Motion
- **Theme**: next-themes
- **Notifications**: Sonner

## 📋 Prerequisites

Before you begin, ensure you have the following installed:
- Node.js 18.0 or later
- npm, yarn, pnpm, or bun

## 🚀 Getting Started

1. **Clone the repository**
   ```bash
   git clone <repository-url>
   cd TryKatch
   ```

2. **Install dependencies**
   ```bash
   npm install
   # or
   yarn install
   # or
   pnpm install
   ```

3. **Set up environment variables**
   ```bash
   cp .env.example .env.local
   ```
   Edit `.env.local` and add your environment variables.

4. **Run the development server**
   ```bash
   npm run dev
   # or
   yarn dev
   # or
   pnpm dev
   ```

5. **Open your browser**
   Navigate to [http://localhost:3000](http://localhost:3000) to see the application.

## 📁 Project Structure

```
src/
├── app/                 # Next.js app router
│   ├── (routes)/       # Route groups
│   ├── common/         # Common layout components
│   ├── components/     # Page-specific components
│   └── pages/          # Page sections
├── components/         # Reusable UI components
│   └── ui/            # shadcn/ui components
├── lib/               # Utility functions
└── types/             # TypeScript type definitions
```

## 🔧 Available Scripts

- `npm run dev` - Start development server with Turbopack
- `npm run build` - Build the application for production
- `npm run start` - Start the production server
- `npm run lint` - Run ESLint for code linting

## 🎨 Customization

### Themes
The application supports both light and dark themes. Theme configuration can be found in:
- `tailwind.config.ts` - Tailwind CSS configuration
- `src/app/globals.css` - Global styles and CSS variables

### Components
UI components are built using shadcn/ui and can be customized in the `src/components/ui/` directory.

## 📱 Deployment

### Vercel (Recommended)
1. Push your code to a Git repository
2. Connect your repository to Vercel
3. Vercel will automatically deploy your application

### Other Platforms
The application can be deployed to any platform that supports Next.js:
- Netlify
- AWS Amplify
- Railway
- DigitalOcean App Platform

## 🤝 Contributing

We welcome contributions! Please see our [Contributing Guidelines](CONTRIBUTING.md) for details.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## � Team

Our experienced team is dedicated to delivering exceptional software solutions:

- **Cedric Justin** - Tech Lead
- **Jean Claude** - Software Developer

## �📞 Contact

For inquiries about TryKatch services:
- Website: [trykatch.com](https://trykatch.com)
- Email: contact@trykatch.com

## 🙏 Acknowledgments

- Built with [Next.js](https://nextjs.org/)
- UI components from [shadcn/ui](https://ui.shadcn.com/)
- Icons from [Lucide](https://lucide.dev/)

## Deploy on Vercel

The easiest way to deploy your Next.js app is to use the [Vercel Platform](https://vercel.com/new?utm_medium=default-template&filter=next.js&utm_source=create-next-app&utm_campaign=create-next-app-readme) from the creators of Next.js.

Check out our [Next.js deployment documentation](https://nextjs.org/docs/app/building-your-application/deploying) for more details.
