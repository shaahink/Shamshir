/** @type {import('jest').Config} */
const { createCjsPreset } = require('jest-preset-angular/presets');
const ngPreset = createCjsPreset();

module.exports = {
  ...ngPreset,
  testMatch: ['<rootDir>/src/app/**/*.spec.ts'],
  testPathIgnorePatterns: ['<rootDir>/tests/', '<rootDir>/src/app/app.component.spec.ts', '<rootDir>/src/app/features/trades/', '<rootDir>/src/app/features/strategies/'],
};
