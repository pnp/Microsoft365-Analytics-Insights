// // ----------------------------------------------------------------------
// // <copyright file="DocumentTranslationManager.cs" company="Microsoft Corporation">
// // Copyright (c) Microsoft Corporation.
// // All rights reserved.
// // THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// // KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// // IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// // PARTICULAR PURPOSE.
// // </copyright>
// // ----------------------------------------------------------------------
// // <summary>O365ManagementApiSubscription.cs</summary>
// // ----------------------------------------------------------------------


namespace WebJob.Office365ActivityImporter.Engine.Entities
{
    /// <summary>
    /// Data class to deserialise the activity API audit-log subscription information into
    /// </summary>
    public class ApiSubscription
    {
        public string contentType { get; set; }

        public string status { get; set; }

        public string webhook { get; set; }
    }
}
