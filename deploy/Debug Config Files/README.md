# Debugging Solution in VS 
To avoid storing sensitive credentials and configuration in git, we have exclusions setup for debug versions of configuration files. The debug templates are here for each project type.
To compile these projects in visual studio, you need to copy one of the templates to your project folder

| Project | Required Template
|-|-|
|App.ControlPanel.WinForms | ControlPanel/App.Debug.config |
| Tests.UnitTests | Unit tests/App.Debug.config
|WebJob.AppInsightsImporter & WebJob.Office365ActivityImporter | Webjobs/App.Debug.config
|Web | Web/Web.Debug.config

Each executable project merges its template with the transform for the build configuration before it builds. `App.ControlPanel.WinForms`, `Tests.UnitTests` and both WebJobs do it like this:

```xml
  <Target Name="TransformAppConfig" BeforeTargets="PrepareForBuild">
    <TransformXml Source="App.Template.config" Transform="App.$(Configuration).config" Destination="App.config" />
  </Target>
```

`Web` does the same with `Web.Template.config` and `Web.$(Configuration).config`, in its `BeforeBuild` target. A Debug build fails at this step until the Debug transform has been copied in. See *Before your first build* in [CONTRIBUTING.md](../../CONTRIBUTING.md#before-your-first-build-create-your-local-config-files) for a script that copies them all.
