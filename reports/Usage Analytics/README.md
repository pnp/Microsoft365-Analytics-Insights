# Report deployment

In this folder you can find both reports to deploy for the Activity Analytics feature.

Please check [the documentation here](/docs/analytics/README.md) for deployment instructions.

## Reports

File | Customizable | Description
-|-|-
Analytics_DataModel_base | No | This is the report that we modify and add new changes, both to the data model and the visuals. The rest of the reports are minified versions of this one. Not part of the release.
Analytics_DataModel | No | Used to deploy the data model only.
Analytics_Report | Yes | Report for end users

## Report generation steps (Development only)

1. Open `Analytics_DataModel_base.pbit` and set up the database connection. Save it as `.pbix` for later.
2. Remove end user pages from the report and save it as `Analytics_DataModel.pbix`.
3. Publish the data model to a workspace in the Power BI service.
4. Export the template to `Analytics_DataModel.pbit`.
4. Reopen `Analytics_DataModel_base.pbix`.
5. Un-hide end user pages and remove the rest.
6. Open "Transform data" and remove everything. Close and apply the changes.
7. Remove the Dates table (DAX) from the report. There should not be any data or parameters left in the report.
8. Connect it to the data model published in (3). Use `Get data`->`Power BI semantic models`.
9. Make sure everything works as expected.
10. Save as template to `Analytics_Report.pbit`.