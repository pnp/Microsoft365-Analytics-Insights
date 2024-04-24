using System.Text.Json;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups
{
    public class JsonObjectToStringLookup : AbstractValueLookup
    {
        public const string PROP_LOOKUP_TYPE_ID_LOOKUP = nameof(JsonObjectToStringLookup);
        public JsonObjectToStringLookup(string paramsJson, bool required) : base(required)
        {
            this.Params = JsonSerializer.Deserialize<JsonObjectToStringParams>(paramsJson);
        }
        public JsonObjectToStringParams Params { get; set; }

        public override bool IsValid => Params != null && Params.IsValid;

        protected override AbstractValueLookup GetChildLookup(string responseVal)
        {
            return AbstractValueLookup.GetListLookup(responseVal);
        }

        protected override Task<string> LookupValue()
        {
            var json = Params.PayLoad.ToString().Replace("\"", @"\""");
            return Task.FromResult(json);
        }

    }
}
