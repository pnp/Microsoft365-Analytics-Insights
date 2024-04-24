using System;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests.FakeEntities
{
    public class FakePagedResult : PageableGraphResponse<FakeResult>
    {
    }

    public class FakeResult
    {
        public Guid RandomGuid { get; set; } = Guid.NewGuid();
        public int Index { get; set; }
        public DateTime Generated { get; set; } = DateTime.Now;

        public override string ToString()
        {
            return $"#{Index}";
        }
    }
}
