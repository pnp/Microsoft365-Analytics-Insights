namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// Stable keys for the messages the user organisation API writes for an administrator: organisation
    /// type and attribute validation, the live attribute test, attribute discovery, changes refused
    /// while an import runs, and the API's own not-found and fault replies.
    /// </summary>
    /// <remarks>
    /// The API reports facts and the portal writes the sentences: each message is sent with one of these
    /// codes and the facts behind it, and the portal words it from <c>userOrgs.message.&lt;code&gt;</c> in
    /// the reader's language. The English text travels alongside only as the fallback for a code the
    /// portal does not know. <c>src/i18n/lint/serverAuthoredText.test.ts</c> reads this file and fails
    /// when the codes and the portal's catalogue drift apart, so add a catalogue entry in the same change
    /// as a code. Values named in a comment are sent with the code.
    /// </remarks>
    public static class UserOrgMessageCodes
    {
        // Organisation types
        public const string NoType = "noType";
        public const string NameRequired = "nameRequired";
        public const string NameTooLong = "nameTooLong"; // max
        public const string DuplicateName = "duplicateName"; // name
        public const string InvalidSource = "invalidSource";
        public const string TypeGone = "typeGone";
        public const string TypeChangedElsewhere = "typeChangedElsewhere"; // name
        public const string ImportRunningChange = "importRunningChange"; // name
        public const string ImportRunningDelete = "importRunningDelete";

        // Attribute names
        public const string AttributeRequired = "attributeRequired";
        public const string AttributeTooLong = "attributeTooLong"; // max
        public const string OpenExtension = "openExtension";
        public const string AttributeHasSpaces = "attributeHasSpaces"; // attribute
        public const string BadDirectoryExtension = "badDirectoryExtension"; // attribute
        public const string TooManyDots = "tooManyDots"; // attribute
        public const string NothingAfterDot = "nothingAfterDot"; // attribute
        public const string NotEmployeeOrgDataProperty = "notEmployeeOrgDataProperty"; // property
        public const string DirectoryExtensionSubProperty = "directoryExtensionSubProperty"; // attribute
        public const string UnknownContainer = "unknownContainer"; // container
        public const string BadSchemaProperty = "badSchemaProperty"; // property
        public const string EmployeeOrgDataContainer = "employeeOrgDataContainer";
        public const string OnPremisesContainer = "onPremisesContainer";
        public const string UnsupportedAttribute = "unsupportedAttribute"; // attribute
        public const string BadOnPremisesAttribute = "badOnPremisesAttribute"; // attribute

        // The live attribute test
        public const string NoTestRequest = "noTestRequest";
        public const string UpnRequired = "upnRequired";
        public const string GraphAuthFailed = "graphAuthFailed";
        public const string UserNotFound = "userNotFound";
        public const string PropertyRejected = "propertyRejected"; // property
        public const string NotAuthorised = "notAuthorised";
        public const string Throttled = "throttled";
        public const string GraphError = "graphError"; // status
        public const string UnreadableResponse = "unreadableResponse";
        public const string MultiValued = "multiValued"; // property
        public const string NoValue = "noValue";
        public const string WouldTruncate = "wouldTruncate"; // max

        // Attribute discovery
        public const string DiscoveryAuthFailed = "discoveryAuthFailed";
        public const string DiscoveryForbidden = "discoveryForbidden";
        public const string DiscoveryGraphError = "discoveryGraphError"; // status
        public const string DiscoveryNoneReturned = "discoveryNoneReturned";
        public const string DiscoveryUnreachable = "discoveryUnreachable";

        // The API's own replies: something asked for that is gone, and requests it would not or could not answer
        public const string JobGone = "jobGone";
        public const string ValueGone = "valueGone";
        public const string NotFromPortal = "notFromPortal";
        public const string Unexpected = "unexpected";
    }
}
