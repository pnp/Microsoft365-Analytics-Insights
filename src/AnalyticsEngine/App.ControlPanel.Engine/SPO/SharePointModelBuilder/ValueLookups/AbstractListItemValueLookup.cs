using CloudInstallEngine.Models;
using Common.DataUtils;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups
{
    /// <summary>
    /// A class that looks up a value for list data. Not necesarily SharePoint bound
    /// </summary>
    public abstract class AbstractValueLookup
    {
        protected const string PROP_NAME_LOOKUP_TYPE = "lookupType";
        protected const string PROP_NAME_LOOKUP_PARAMS = "lookupParams";
        protected const string PROP_NAME_REQUIRED = "required";

        public AbstractValueLookup(bool required)
        {
            this.Required = required;
        }

        public static AbstractValueLookup GetListLookup(string json)
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
                    case JsonObjectToStringLookup.PROP_LOOKUP_TYPE_ID_LOOKUP:
                        return new JsonObjectToStringLookup(lookupParamsElem.ToString(), required);
                    default:
                        break;
                }
            }

            throw new ArgumentOutOfRangeException("Invalid lookup function data");
        }

        public static bool IsListLookupDefintion(string json)
        {
            var obj = JsonDocument.Parse(json);

            JsonElement lookupTypeElem, lookupParamsElem;
            if (obj.RootElement.TryGetProperty(PROP_NAME_LOOKUP_TYPE, out lookupTypeElem) && obj.RootElement.TryGetProperty(PROP_NAME_LOOKUP_PARAMS, out lookupParamsElem))
            {
                switch (lookupTypeElem.ToString())
                {
                    case IdValueFromAnotherListValueLookup.PROP_LOOKUP_TYPE_ID_LOOKUP:
                        return true;

                    case InsertValueIfNotExists.PROP_LOOKUP_TYPE_ID_LOOKUP:
                        return true;
                    case ThumbnailImageProvisionAndLookup.PROP_LOOKUP_TYPE_ID_LOOKUP:
                        return true;
                    case JsonObjectToStringLookup.PROP_LOOKUP_TYPE_ID_LOOKUP:
                        return true;
                    default:
                        break;
                }
            }

            return false;
        }

        public async Task<int> GetLookupValueInt()
        {
            var s = await GetLookupValue();
            var i = 0;
            int.TryParse(s, out i);
            return i;
        }

        public async Task<string> GetLookupValue()
        {
            var responseVal = string.Empty;
            try
            {
                responseVal = await LookupValue();
            }
            catch (LookupFailedException)
            {
                if (Required)
                {
                    throw;
                }
            }

            if (responseVal != null)
            {
                // Chained lookup?
                if (responseVal.IsJson() && IsListLookupDefintion(responseVal))
                {
                    // Another lookup?
                    var childLookup = GetChildLookup(responseVal);
                    return await childLookup.GetLookupValue();
                }
                else
                    return responseVal;
            }
            else
            {
                return default;
            }
        }
        protected abstract Task<string> LookupValue();
        protected abstract AbstractValueLookup GetChildLookup(string responseVal);

        public bool Required { get; set; }

        public abstract bool IsValid { get; }
    }

    public class LookupFailedException : InstallException
    {
        public LookupFailedException(string message) : base(message)
        {
        }
    }
    public class LookupNoResultsException : LookupFailedException
    {
        public LookupNoResultsException(string paramsUsed) : base("No results found for SharePoint list lookup. Params: " + paramsUsed)
        {
        }
    }
    public class LookupTooManyResultsException : LookupFailedException
    {
        public LookupTooManyResultsException(string paramsUsed) : base("Too many results for SharePoint list lookup. Params: " + paramsUsed)
        {
        }
    }

}
