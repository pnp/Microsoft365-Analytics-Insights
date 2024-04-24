using Microsoft.SharePoint.Client;
using System.Text.Json;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups
{
    /// <summary>
    /// Find an ID from another list item
    /// </summary>
    public class IdValueFromAnotherListValueLookup : AbstractSPListItemValueLookup
    {
        public const string PROP_LOOKUP_TYPE_ID_LOOKUP = "IdLookup";

        public LookupidParams Params { get; set; }

        public override bool IsValid => Params != null && Params.IsValid;

        public IdValueFromAnotherListValueLookup(ClientContext clientContext, string paramsJson, bool required) : base(clientContext, required)
        {
            this.Params = JsonSerializer.Deserialize<LookupidParams>(paramsJson);
        }

        protected override async Task<string> LookupValue()
        {
            var list = _clientContext.Web.GetListByTitle(Params.ListTitle);

            var query = new CamlQuery();
            query.ViewXml = $"<View><Query><Where><Eq><FieldRef Name=\"{Params.FieldName}\"/><Value Type=\"Text\">{Params.FieldValue}</Value></Eq></Where></Query></View>";
            var results = list.GetItems(query);
            _clientContext.Load(results);

            await _clientContext.ExecuteQueryAsync();
            if (results.Count == 0)
            {
                throw new LookupNoResultsException(this.Params.ToString());
            }
            else if (results.Count == 1)
            {
                return results[0].Id.ToString();
            }
            else
            {
                throw new LookupTooManyResultsException(this.Params.ToString());
            }
        }
    }
}
