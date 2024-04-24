using App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups;
using CloudInstallEngine.Models;
using Common.DataUtils;
using Microsoft.SharePoint.Client;
using Newtonsoft.Json.Linq;
using OfficeDevPnP.Core.Framework.Provisioning.Model;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Field = OfficeDevPnP.Core.Framework.Provisioning.Model.Field;
using View = OfficeDevPnP.Core.Framework.Provisioning.Model.View;

namespace App.ControlPanel.Engine.SharePointModelBuilder
{
    public class SiteBuilder
    {
        public static ListInstance BuildGenericList(string name, string url, bool addToQuickLaunch, SPField[] fields)
        {
            return BuildList(name, url, false, addToQuickLaunch, fields);
        }
        public static ListInstance BuildDocLib(string name, string url, bool addToQuickLaunch)
        {
            return BuildList(name, url, true, addToQuickLaunch, new SPField[] { });
        }
        static ListInstance BuildList(string name, string url, bool isDocLib, bool addToQuickLaunch, SPField[] fields)
        {
            var listType = (Int32)ListTemplateType.GenericList;
            if (isDocLib)
            {
                listType = (Int32)ListTemplateType.DocumentLibrary;
            }
            var list = new ListInstance()
            {
                Title = name,
                Url = url,
                TemplateType = listType,
                EnableAttachments = !isDocLib,
                OnQuickLaunch = addToQuickLaunch
            };

            // Add default fields
            if (!isDocLib)
            {
                list.FieldRefs.Add(new FieldRef("LinkTitle"));
            }
            else
            {
                list.FieldRefs.Add(new FieldRef("DocIcon"));
            }

            foreach (var f in fields)
            {
                if (f.ID == Guid.Empty)
                    throw new ArgumentException(nameof(f.ID), $"Invalid SPO schema - field '{f.Name}' has no ID set");

                // Add field to list definition
                list.Fields.Add(new Field { SchemaXml = f.ToXmlString() });
                list.FieldRefs.Add(new FieldRef(f.Name));
            }

            if (!isDocLib)
            {
                var viewFieldsXml = string.Empty;
                foreach (var f in fields)
                {
                    // Build view XML
                    if (f.IncludeInDefaultView)
                        viewFieldsXml += $"<FieldRef Name=\"{f.Name}\" />" + Environment.NewLine;
                }

                var view = new View()
                {
                    SchemaXml = "<View Name=\"{" + Guid.NewGuid().ToString().ToUpper() + "}\" DefaultView=\"TRUE\" MobileView=\"TRUE\" MobileDefaultView=\"TRUE\" Type=\"HTML\" DisplayName=\"All Items\" Url=\"{site}/" + url + "/AllItems.aspx\" Level=\"1\" BaseViewID=\"1\" ContentTypeID=\"0x\" ImageUrl=\"/_layouts/15/images/generic.png?rev=47\"> " +
                                    "<Query><OrderBy><FieldRef Name=\"ID\" /></OrderBy></Query>" +
                                    "<ViewFields>" + viewFieldsXml + "</ViewFields>" +
                                    "<RowLimit Paged=\"TRUE\">100</RowLimit>" +
                                    "<Aggregations Value=\"Off\" /><JSLink>clienttemplates.js</JSLink>" +
                                    "<CustomFormatter />" +
                                    "<ViewData />" +
                                "</View>"
                };
                list.Views.Add(view);
            }

            return list;
        }

        /// <summary>
        /// Insert unique list data from Json
        /// </summary>
        public static async Task<int> ApplyListData(string json, ClientContext clientContext, Guid listId)
        {
            var list = clientContext.Web.GetListById(listId);
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
