using App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups;
using CloudInstallEngine.Models;
using DataUtils;
using Microsoft.SharePoint.Client;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SharePointModelBuilder
{
    public class SiteBuilder
    {
        /// <summary>
        /// Insert unique list data from Json
        /// </summary>
        public static async Task<int> ApplyListData(string json, ClientContext clientContext, Guid listId)
        {
            var list = clientContext.Web.Lists.GetById(listId);
            clientContext.Load(list, l => l.Title);
            await clientContext.ExecuteQueryAsync();

            var array = JArray.Parse(json);
            var objectsToInsert = new List<JObject>();

            // Look for duplicates
            foreach (var obj in array.Children<JObject>())
            {
                var query = new CamlQuery();
                var fieldsQuery = string.Empty;
                foreach (var singleProp in obj.Properties())
                {
                    var name = singleProp.Name;
                    var value = singleProp.Value.ToString();

                    if (!value.IsJson())
                    {
                        if (!string.IsNullOrEmpty(value))
                        {
                            fieldsQuery += $"<Eq><FieldRef Name=\"{name}\"/><Value Type=\"Text\">{value}</Value></Eq>";
                        }
                    }
                }

                query.ViewXml = $"<View><Query><Where>{fieldsQuery}</Where></Query></View>";
                var results = list.GetItems(query);
                clientContext.Load(results);

                await clientContext.ExecuteQueryAsync();
                if (results.Count == 0)
                {
                    objectsToInsert.Add(obj);
                }
            }

            // Build updates
            var updates = new List<Dictionary<string, string>>();
            foreach (var obj in objectsToInsert)
            {
                var objProps = new Dictionary<string, string>();
                foreach (var singleProp in obj.Properties())
                {
                    var value = singleProp.Value.ToString();
                    var listItemValue = value;

                    // Do we need to do a lookup?
                    if (value.IsJson())
                    {
                        // Do we have a lookup for this value?
                        AbstractValueLookup lookup = null;
                        try
                        {
                            lookup = AbstractSPListItemValueLookup.GetSPListLookup(clientContext, value);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Json was something else
                        }

                        if (lookup != null && lookup.IsValid)
                        {
                            listItemValue = await lookup.GetLookupValue();
                        }
                    }

                    if (!string.IsNullOrEmpty(listItemValue))
                    {
                        objProps.Add(singleProp.Name, listItemValue);
                    }
                }
                updates.Add(objProps);
            }

            // Insert unique
            foreach (var update in updates)
            {
                if (list.BaseType == BaseType.GenericList)
                {
                    var listItemCreationInformation = new ListItemCreationInformation();
                    var newItem = list.AddItem(listItemCreationInformation);

                    foreach (var singleProp in update)
                    {
                        newItem[singleProp.Key] = singleProp.Value;
                    }
                    newItem.Update();
                }
                else
                {
                    throw new InstallException($"List '{list.Title}' is not a generic list & not supported for this operation.");
                }

                try
                {
                    await clientContext.ExecuteQueryAsync();
                }
                catch (ServerException ex)
                {
                    throw new InstallException($"Error adding list item to list '{list.Title}' - {ex.Message}");
                }
            }


            return objectsToInsert.Count;
        }
    }
}
