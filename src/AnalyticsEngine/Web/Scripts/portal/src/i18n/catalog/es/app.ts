import type { app as en } from '../en/app';

/**
 * Spanish (es-ES) text for the application shell.
 *
 * Typed against the English module, so a key added there without a translation here fails the
 * build rather than reaching a customer as English text inside a Spanish page.
 *
 * Product and feature names Microsoft ships untranslated in Spanish - "Copilot", "Teams", "DLP" -
 * are deliberately left as they are. A Spanish-speaking M365 administrator sees those words in
 * their own admin centre, so translating them here would make the portal harder to follow, not
 * easier.
 */
const app: Record<keyof typeof en, string> = {
  'app.signOut': 'Cerrar sesi\u00f3n',
  'app.nav.collapse': 'Contraer la navegaci\u00f3n',
  'app.nav.expand': 'Expandir la navegaci\u00f3n',
  'app.nav.ariaLabel': 'Navegaci\u00f3n de {area}',

  'app.language.label': 'Idioma',
  'app.language.choose': 'Cambiar idioma',
  'app.language.current': 'Idioma: {language}',
  'app.print.buildLabel': 'compilación {build}',
  'app.print.developmentBuild': 'compilación de desarrollo',

  'app.area.insights': 'An\u00e1lisis',
  'app.area.admin': 'Administraci\u00f3n',

  'app.navGroup.monitoring': 'Supervisi\u00f3n',
  'app.navGroup.manage': 'Administrar',

  'app.route.overview': 'Resumen',
  'app.route.reports': 'Informes',
  'app.route.teamsExplorer': 'Explorador de Teams',
  'app.route.webActivity': 'Actividad web',
  'app.route.copilotAdoption': 'Adopci\u00f3n de Copilot',
  'app.route.licenceActivity': 'Actividad de licencias',
  'app.route.agentCosts': 'Costes de agentes',
  'app.route.dlp': 'Impacto de DLP',
  'app.route.health': 'Estado del servicio',
  'app.route.installLog': 'Registro de instalaci\u00f3n',
  'app.route.profiling': 'Generaci\u00f3n de perfiles',
  'app.route.teamsPermissions': 'Permisos de Teams',
  'app.route.userLookup': 'Consulta de datos de usuario',
  'app.route.configuration': 'Configuraci\u00f3n del servicio',
};

export default app;
