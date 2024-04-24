# Debugging Solution in VS 
To avoid storing sensitive credentials and configuration in git, we have exclusions setup for debug versions of configuration files. The debug templates are here for each project type.
To compile these projects in visual studio, you need to copy one of the templates to your project folder

| Project | Required Template
|-|-|
|App.ControlPanel.WinForms | ControlPanel/App.Debug.config |
| Tests.UnitTests | Unit tests/App.Debug.config
|WebJob.AppInsightsImporter & WebJob.Office365ActivityImporter | Webjobs/App.Debug.config
|Web | Web/Web.Debug.config

Each executable project is set to merge the appropriate configuration file on build:

```xml
  <Target Name="BeforeBuild">
    <TransformXml Source="App.Template.config" Transform="App.$(Configuration).config" Destination="App.config" />
  </Target>
```
