# Jest Configuration

## ES Module Setup

This project uses ES modules for Jest configuration (`jest.config.mjs`) instead of CommonJS to maintain consistency with the modern JavaScript/TypeScript setup.

### Key Configuration Features

1. **ES Module Imports**: Uses `import` instead of `require()`
2. **Next.js Integration**: Leverages `next/jest` for seamless Next.js testing
3. **Path Mapping**: Supports `@/` alias for clean imports
4. **TypeScript Support**: Full TypeScript testing support
5. **Coverage Reporting**: Configured with coverage thresholds
6. **Test Environment**: Uses `jsdom` for React component testing

### File Structure

```
jest.config.mjs          # Main Jest configuration (ES module)
jest.setup.js           # Jest setup file for global test configuration
src/__tests__/          # Test files directory
```

### Available Test Commands

- `npm run test` - Run all tests
- `npm run test:watch` - Run tests in watch mode
- `npm run test:coverage` - Run tests with coverage report
- `npm run type-check` - Check TypeScript types

### Testing Best Practices

1. **Component Testing**: Use React Testing Library for component tests
2. **Mocking**: Mock external dependencies and components as needed
3. **Coverage**: Maintain coverage thresholds (70% for all metrics)
4. **Accessibility**: Test for accessibility features using jest-dom matchers

### Troubleshooting

If you encounter module resolution issues:
1. Check that path aliases are correctly configured in `jest.config.mjs`
2. Ensure `@testing-library/jest-dom` is imported in test files
3. Verify that mocked modules match the actual import paths
