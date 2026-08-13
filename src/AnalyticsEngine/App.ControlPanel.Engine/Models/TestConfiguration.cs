namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Config used to run install tests
    /// </summary>
    public class TestConfiguration
    {
        public TestConfiguration() { }

        public string SQLConnectionString { get; set; }

        public bool IsValid => !string.IsNullOrEmpty(SQLConnectionString);
    }
}
