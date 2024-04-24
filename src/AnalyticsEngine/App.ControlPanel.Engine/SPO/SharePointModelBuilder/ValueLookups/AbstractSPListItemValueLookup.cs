using Microsoft.SharePoint.Client;
using System.Text.Json;

namespace App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups
{
    /// <summary>
    /// Data lookup from SharePoint
    /// </summary>
    public abstract class AbstractSPListItemValueLookup : AbstractValueLookup
    {
        protected readonly ClientContext _clientContext;
        protected AbstractSPListItemValueLookup(ClientContext clientContext, bool required) : base(required)
        {
            _clientContext = clientContext;
        }


        protected override AbstractValueLookup GetChildLookup(string snip)
        {
            return AbstractSPListItemValueLookup.GetSPListLookup(_clientContext, snip);
        }

        public static AbstractValueLookup GetSPListLookup(ClientContext clientContext, string json)
        {
            var obj = JsonDocument.Parse(json);

            var required = false;
            JsonElement requiredElem, lookupTypeElem, lookupParamsElem;
            if (obj.RootElement.TryGetProperty(PROP_NAME_REQUIRED, out requiredElem))
            {
                bool.TryParse(requiredElem.ToString(), out required);
            }
            if (obj.RootElement.TryGetProperty(PROP_NAME_LOOKUP_TYPE, out lookupTypeElem) && obj.RootElement.TryGetProperty(PROP_NAME_LOOKUP_PARAMS, out lookupParamsElem))
            {
                switch (lookupTypeElem.ToString())
                {
                    case IdValueFromAnotherListValueLookup.PROP_LOOKUP_TYPE_ID_LOOKUP:
                        return new IdValueFromAnotherListValueLookup(clientContext, lookupParamsElem.ToString(), required);

                    case InsertValueIfNotExists.PROP_LOOKUP_TYPE_ID_LOOKUP:
                        return new InsertValueIfNotExists(clientContext, lookupParamsElem.ToString(), required);

                    case ThumbnailImageProvisionAndLookup.PROP_LOOKUP_TYPE_ID_LOOKUP:
                        return new ThumbnailImageProvisionAndLookup(clientContext, lookupParamsElem.ToString(), required);

                    default:
                        break;
                }
            }

            return AbstractValueLookup.GetListLookup(json);
        }
    }
}
