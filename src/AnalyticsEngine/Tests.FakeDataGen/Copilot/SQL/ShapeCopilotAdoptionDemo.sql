/*
   The hand-run Copilot shaper has been retired. Its adoption personas, department
   variation, dormant/never-used cohorts, unlicensed Chat demand and Cowork/agent
   scenarios now belong to the one-command, seeded demo generator:

       Tests.FakeDataGen.exe demo --database ContosoDemo_Example
       Tests.FakeDataGen.exe demo --help

   The command creates a NEW LocalDB database, also populates current licence
   assignments, historical workload/official Copilot reports and web/Power BI
   inputs, and verifies completion. It never reshapes an existing database.
   Keeping an executable second shaper here would let the two models drift and
   retain a dangerous way to run demo transformations against customer data.
*/
;THROW 51000, 'This shaper is retired. Run Tests.FakeDataGen.exe demo --help; a NEW ContosoDemo_ LocalDB target is required.', 1;
