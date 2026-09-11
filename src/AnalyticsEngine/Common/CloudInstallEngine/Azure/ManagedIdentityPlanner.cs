using Azure.ResourceManager.Models;

namespace CloudInstallEngine.Azure
{
    /// <summary>
    /// Decides what to send in a resource's <c>identity</c> block when the installer needs to turn a
    /// system-assigned managed identity on, without discarding user-assigned identities that an operator
    /// attached themselves.
    /// </summary>
    /// <remarks>
    /// ARM REPLACES the whole <c>identity</c> block on a write - it does not merge it. So patching a
    /// resource with a bare <see cref="ManagedServiceIdentityType.SystemAssigned"/> silently deletes every
    /// user-assigned identity already on it, and nothing in the response says so. Re-running the installer
    /// must never destroy operator configuration (the same reason automation variables are only created
    /// when absent), so any user-assigned identities already present are read back and re-sent alongside.
    ///
    /// The identity type has to be CHOSEN rather than fixed: ARM rejects
    /// <see cref="ManagedServiceIdentityType.SystemAssignedUserAssigned"/> when the
    /// <c>userAssignedIdentities</c> map is empty, so a resource that has none must be sent plain
    /// <see cref="ManagedServiceIdentityType.SystemAssigned"/>. Always sending the combined type would
    /// swap a silent data-loss bug for a hard failure on the common case.
    /// </remarks>
    public static class ManagedIdentityPlanner
    {
        /// <summary>
        /// True when <paramref name="existing"/> has no usable system-assigned identity and one must be
        /// enabled. A user-assigned-only identity reports a null <c>PrincipalId</c> at the top level, so it
        /// correctly counts as "needs one" - it is the case that used to trigger the destructive patch.
        /// </summary>
        public static bool NeedsSystemAssignedIdentity(ManagedServiceIdentity existing)
        {
            return existing == null || existing.PrincipalId == null;
        }

        /// <summary>
        /// Builds the identity to send so the resource ends up with a system-assigned identity while
        /// keeping every user-assigned identity it already has.
        /// </summary>
        /// <param name="existing">
        /// The identity currently on the resource, or <c>null</c> when it has none.
        /// </param>
        public static ManagedServiceIdentity BuildSystemAssignedPreservingUserAssigned(ManagedServiceIdentity existing)
        {
            var userAssigned = existing?.UserAssignedIdentities;
            if (userAssigned == null || userAssigned.Count == 0)
            {
                return new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned);
            }

            var identity = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssignedUserAssigned);
            foreach (var resourceId in userAssigned.Keys)
            {
                // An empty value is the request shape ARM expects: clientId / principalId are read-only
                // outputs, so the identity is referenced by resource id alone.
                identity.UserAssignedIdentities[resourceId] = new UserAssignedIdentity();
            }

            return identity;
        }
    }
}
