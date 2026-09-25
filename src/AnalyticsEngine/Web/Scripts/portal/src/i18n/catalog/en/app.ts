/**
 * English text for the application shell: the brand bar, the area switcher, the left navigation
 * and the language picker.
 *
 * The navigation labels live here rather than in `navigation.tsx` because that route table is a
 * module-level constant, evaluated once at import time - long before a language is known. It
 * carries `labelKey`s, and the shell resolves them on every render so switching language relabels
 * the nav without a reload.
 *
 * Every key here must have a Spanish counterpart in `../es/app.ts`; the type of that module makes
 * a missing one a build failure.
 */
export const app = {
  'app.signOut': 'Sign out',
  'app.nav.collapse': 'Collapse navigation',
  'app.nav.expand': 'Expand navigation',
  'app.nav.ariaLabel': '{area} navigation',

  'app.language.label': 'Language',
  'app.language.choose': 'Change language',
  'app.language.current': 'Language: {language}',
  'app.print.buildLabel': 'build {build}',
  'app.print.developmentBuild': 'development build',

  'app.area.insights': 'Insights',
  'app.area.admin': 'Administration',

  'app.navGroup.monitoring': 'Monitoring',
  'app.navGroup.manage': 'Manage',

  'app.route.overview': 'Overview',
  'app.route.reports': 'Reports',
  'app.route.teamsExplorer': 'Teams Explorer',
  'app.route.webActivity': 'Web activity',
  'app.route.copilotAdoption': 'Copilot Adoption',
  'app.route.licenceActivity': 'Licence activity',
  'app.route.agentCosts': 'Agent costs',
  'app.route.dlp': 'DLP impact',
  'app.route.health': 'Service health',
  'app.route.installLog': 'Install log',
  'app.route.profiling': 'Profiling',
  'app.route.teamsPermissions': 'Teams permissions',
  'app.route.userLookup': 'User data lookup',
  'app.route.configuration': 'Service configuration',
} as const;

export default app;
