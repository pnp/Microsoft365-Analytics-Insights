import { pathToFileURL } from 'node:url';

export function deploymentTarget(event, ref, selected = 'none') {
  if (event === 'push' && ref === 'refs/heads/main') return 'production';
  if (event !== 'workflow_dispatch') return 'none';
  if (selected === 'none') return 'none';
  if (selected === 'development' && ref === 'refs/heads/dev') return selected;
  if (selected === 'production' && ref === 'refs/heads/main') return selected;
  throw new Error('Development deploys require dev; production deploys require main. Choose none for a build-only run.');
}

export function deploymentResource(target, config) {
  const app = target === 'development' ? config.developmentApp : config.productionApp;
  const group = target === 'development' ? config.developmentGroup : config.productionGroup;
  if (!['development', 'production'].includes(target) || !app || !group) {
    throw new Error('The selected deployment target is not configured. No production fallback is allowed.');
  }
  if (target === 'development' && (!config.productionApp || app.toLowerCase() === config.productionApp.toLowerCase())) {
    throw new Error('Development must use a different App Service from production.');
  }
  return { app, group };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (process.argv[2] === 'target') {
    console.log(`target=${deploymentTarget(process.env.CI_EVENT, process.env.CI_REF, process.env.DEPLOYMENT || 'none')}`);
  } else if (process.argv[2] === 'resource') {
    const resource = deploymentResource(process.env.DEPLOYMENT, {
      productionApp: process.env.PRODUCTION_APP,
      productionGroup: process.env.PRODUCTION_GROUP,
      developmentApp: process.env.DEVELOPMENT_APP,
      developmentGroup: process.env.DEVELOPMENT_GROUP,
    });
    console.log(`app=${resource.app}\ngroup=${resource.group}`);
  } else {
    throw new Error('Expected target or resource.');
  }
}
