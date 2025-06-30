# Contributing to TryKatch

Thank you for considering contributing to TryKatch! This document provides guidelines and information for contributors.

## 🤝 How to Contribute

### Reporting Issues

1. **Check existing issues** first to avoid duplicates
2. **Use the issue template** when creating new issues
3. **Provide detailed information** including:
   - Steps to reproduce
   - Expected vs actual behavior
   - Screenshots (if applicable)
   - Environment details

### Development Workflow

1. **Fork the repository**
   ```bash
   git clone https://github.com/your-username/trykatch.git
   cd trykatch
   ```

2. **Create a feature branch**
   ```bash
   git checkout -b feature/your-feature-name
   ```

3. **Set up the development environment**
   ```bash
   npm install
   cp .env.example .env.local
   npm run dev
   ```

4. **Make your changes**
   - Follow the coding standards below
   - Add tests if applicable
   - Update documentation if needed

5. **Test your changes**
   ```bash
   npm run lint
   npm run build
   ```

6. **Commit your changes**
   ```bash
   git add .
   git commit -m "feat: add your feature description"
   ```

7. **Push and create a PR**
   ```bash
   git push origin feature/your-feature-name
   ```

## 📋 Coding Standards

### TypeScript
- Use TypeScript for all new code
- Define proper types and interfaces
- Avoid `any` type unless absolutely necessary

### React Components
- Use functional components with hooks
- Follow the naming convention: PascalCase for components
- Use meaningful prop names and add proper TypeScript types

### File Structure
- Place reusable components in `src/components/`
- Use kebab-case for file names
- Group related files in directories

### Styling
- Use Tailwind CSS classes
- Follow mobile-first responsive design
- Use CSS variables for theme consistency

### Code Organization
```typescript
// Component imports first
import React from 'react'
import { Component } from './component'

// Third-party imports
import { clsx } from 'clsx'

// Local imports
import { utils } from '@/lib/utils'

// Types
interface Props {
  title: string
  description?: string
}

// Component
export function MyComponent({ title, description }: Props) {
  // ... component logic
}
```

## 🧪 Testing Guidelines

- Write unit tests for utility functions
- Test component functionality
- Ensure responsive design works across devices
- Test accessibility features

## 📝 Commit Message Format

Use conventional commits format:

```
type(scope): description

Types:
- feat: new feature
- fix: bug fix
- docs: documentation changes
- style: formatting changes
- refactor: code refactoring
- test: adding tests
- chore: maintenance tasks

Examples:
feat(ui): add dark mode toggle
fix(contact): resolve form validation issue
docs(readme): update installation instructions
```

## 🔍 Code Review Process

1. **All changes require a pull request**
2. **At least one approval** from a maintainer
3. **All checks must pass** (linting, building)
4. **Keep PRs focused** - one feature per PR
5. **Update documentation** if needed

## 🚀 Performance Guidelines

- Optimize images and assets
- Use Next.js built-in optimization features
- Minimize bundle size
- Follow React performance best practices

## ♿ Accessibility

- Use semantic HTML elements
- Provide alt text for images
- Ensure keyboard navigation works
- Maintain proper color contrast
- Test with screen readers

## 🎨 Design Guidelines

- Follow the existing design system
- Use consistent spacing and typography
- Maintain brand colors and fonts
- Ensure mobile responsiveness

## 📞 Getting Help

- **Documentation**: Check the README and inline comments
- **Issues**: Search existing issues or create a new one
- **Discussions**: Use GitHub Discussions for questions
- **Contact**: Reach out to maintainers directly if needed

## 📄 License

By contributing, you agree that your contributions will be licensed under the same license as the project.

Thank you for contributing to TryKatch! 🚀
