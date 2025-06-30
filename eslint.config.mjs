import { dirname } from "path";
import { fileURLToPath } from "url";
import { FlatCompat } from "@eslint/eslintrc";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const compat = new FlatCompat({
  baseDirectory: __dirname,
});

const eslintConfig = [
  ...compat.extends("next/core-web-vitals", "next/typescript"),
  {
    rules: {
      "@typescript-eslint/no-explicit-any": "off", // Allow explicit any type
    },
  },
  {
    files: ["**/*.config.{js,mjs,cjs}", "jest.setup.js"],
    rules: {
      "@typescript-eslint/no-require-imports": "off", // Allow require in config files
    },
  },
];

export default eslintConfig;
