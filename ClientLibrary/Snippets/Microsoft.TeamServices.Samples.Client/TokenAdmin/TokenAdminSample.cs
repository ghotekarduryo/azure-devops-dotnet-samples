﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Graph.Client;
using Microsoft.VisualStudio.Services.TokenAdmin.Client;
using Microsoft.VisualStudio.Services.WebApi;

namespace Microsoft.TeamServices.Samples.Client.TokenAdmin
{
    /// <summary>
    /// This sample shows how you can use the VSTS REST APIs to find and revoke 
    /// personal access tokens (PATs) for users in your organization.
    /// It also shows how to create revocation rules that prevent access through other OAuth credentials,
    /// such as self-describing session tokens.
    /// The sample is written using our C# client libraries, 
    /// but is commented with the HTTP calls that you can make to perform these same operations directly over the wire.
    /// </summary>
    /// <remarks>
    /// <p>
    /// NOTE ON REQUIRED PERMISSIONS: To be able to call of all the endpoints in this sample, 
    /// you will need to be an administrator of the target organization.
    /// Non-administrators will receive an authorization error.
    /// </p>
    /// <p>
    /// NOTE ON CONNECTION DETAILS: For this sample, we will be using the connection from our current client context, 
    /// which includes our base URL and our authentication credentials through the parameters passed in through the runner.
    /// If you are calling these endpoints directly over the wire,
    /// your base URL will be => https://{yourOrganization}.vssps.visualstudio.com
    /// and the recommended approach for authentication is to acquire an ADAL access token and pass it in the header => Authorization: Bearer {yourADALAccessToken}
    /// For further guidance on how to acquire an ADAL access token, see:
    /// https://github.com/Microsoft/vsts-auth-samples/tree/master/ManagedClientConsoleAppSample
    /// For further guidance on other authentication options, see:
    /// https://docs.microsoft.com/en-us/vsts/integrate/get-started/authentication/authentication-guidance?view=vsts
    /// </p>
    /// </remarks>
    [ClientSample(TokenAdminResourceIds.AreaName)]
    public class TokenAdminSample : ClientSample
    {
        // The areas of the VSTS REST API / HTTP clients that we will be using for these administration tasks are:
        // - the Graph endpoints, corresponding to the path pattern: /_apis/graph/*
        // - the TokenAdmin endpoints, corresponding to the path pattern: /_apis/tokenAdmin/*
        //   The C# client for this latter area may not be available until the next release of our client libraries,
        //   but the REST endpoints themselves should be available at the time of publishing.

        [ClientSampleMethod(TokenAdminResourceIds.AreaName, TokenAdminResourceIds.PersonalAccessTokensResource)]
        public List<Guid> GetPersonalAccessTokenAuthorizationIdsForSpecificUsersInYourOrganization()
        {
            var graphHttpClient = Context.Connection.GetClient<GraphHttpClient>();

            // In this example, we assume that you already have a set of VSIDs or subject descriptors 
            // for the users whose PAT tokens you want to retrieve. If this is not the case, see the sample for all users further below. 
            // If you have VSIDs, you will need to first convert them to subject descriptors as shown below.
            // If you have subject descriptors, you can go directly to the code in GetPersonalAccessTokenDetailsForASetOfUsers.

            // (1) CONVERTING VSIDS to SUBJECT DESCRIPTORS

            var vsids = new[]
            {
                new Guid("e3b2f97f-8fd8-4086-82b4-e7d878e89c37"),
                new Guid("1b90428b-d3f0-4703-812b-e497f08f183a"),
            };

            // For each VSID, we ask Graph for the corresponding subject descriptor:
            // HTTP: GET /_apis/graph/descriptors/{vsid}
            //       => 200 OK {
            //                   "value":"{subjectDescriptor}",
            //                      …
            //                 }
            var subjectDescriptors = vsids.Select(vsid => graphHttpClient.GetDescriptorAsync(vsid).SyncResult()).Select(result => result.Value);

            // (2) RETRIEVING PERSONAL ACCESS TOKEN AUTHORIZATION IDS FOR A SET OF SUBJECT DESCRIPTORS => see (4) below.
            return GetPersonalAccessTokenAuthorizationIdsForASetOfUsers(subjectDescriptors);
        }

        [ClientSampleMethod(TokenAdminResourceIds.AreaName, TokenAdminResourceIds.PersonalAccessTokensResource)]
        public List<Guid> GetPersonalAccessTokenAuthorizationIdsForAllUsersInYourOrganization()
            => GetPersonalAccessTokenAuthorizationIdsForAllUsersInYourOrganization(thenForEachPage: null);

        private List<Guid> GetPersonalAccessTokenAuthorizationIdsForAllUsersInYourOrganization(Action<List<Guid>> thenForEachPage)
        {
            var graphHttpClient = Context.Connection.GetClient<GraphHttpClient>();

            // (3) RETRIEVING SUBJECT DESCRIPTORS FOR ALL USERS

            // First, we will use the Graph endpoints to find the subject descriptors of all users in the organization.
            // This is a paginated API, so you may need to keep track of the continuation token
            // and send multiple requests depending on the number of users in your organization.

            var authorizationIds = new List<Guid>();

            string userContinuationToken = null;
            do
            {
                // Make a call to retrieve the next page of users:
                // HTTP: GET /_apis/graph/users[?continuationToken={userContinuationToken}]
                //       => 200 OK 
                //          X-MS-ContinuationToken: {userContinuationToken}   
                //          {
                //            "count":n,
                //            "value":[{
                //               "descriptor":"{user.Descriptor}",
                //                 …
                //          },…]
                var pageOfUsers = graphHttpClient.ListUsersAsync(continuationToken: userContinuationToken).SyncResult();

                // Over the wire, the continuation token (if any) is returned in the X-MS-ContinuationToken response header as shown above.
                // In C#, this is parsed for us as a property of the result object:
                userContinuationToken = pageOfUsers.ContinuationToken?.SingleOrDefault();

                // Now that we have a page of users, we use their descriptors to get the authorization IDs for all of their personal access tokens:
                var authorizationIdsForOnePageOfUsers = GetPersonalAccessTokenAuthorizationIdsForASetOfUsers(pageOfUsers.GraphUsers.Select(user => user.Descriptor));

                // And once we have the authorization IDs for the current page,
                // we can chain in another operation, such as revoking the authorizations (see below), in a paginated way.
                // This pattern is recommended over pulling down the full list of authorization IDs 
                // and trying to pass them all back up later on to help callers remain with resource governance restrictions.
                thenForEachPage?.Invoke(authorizationIdsForOnePageOfUsers);

                // If desired, you may also want to track the full list of authorization IDs for local usage only.
                authorizationIds.AddRange(authorizationIdsForOnePageOfUsers);
            }
            // Repeat the above while we still have more pages of users:
            while (!string.IsNullOrEmpty(userContinuationToken));

            return authorizationIds;
        }

        private List<Guid> GetPersonalAccessTokenAuthorizationIdsForASetOfUsers(IEnumerable<SubjectDescriptor> userDescriptors)
        {
            var tokenAdminHttpClient = Context.Connection.GetClient<TokenAdminHttpClient>();

            // (4) RETRIEVING PERSONAL ACCESS TOKEN AUTHORIZATION IDS FOR A SET OF SUBJECT DESCRIPTORS

            // Given a user's subject descriptor, you can retrieve the authorization IDs that correspond
            // to their personal access tokens as shown below.
            // This is a paginated API, so you may need to keep track of the continuation token
            // and send multiple requests depending on the number of PATs associated with each user.
            // The page size is client-configurable up to a server-side limit.
            // We pass null below to use the default (recommended), but you can adjust it if desired.
            // However, keep in mind that you will receive an error if you pass too high a value.
            int? patPageSize = null;

            var authorizationIds = new List<Guid>();

            foreach (var userDescriptor in userDescriptors)
            {
                Guid? patContinuationToken = null;
                do
                {
                    // Make a call to retrieve the next page of PATs:
                    // HTTP: GET /_apis/tokenAdmin/personalAccessTokens/{userDescriptor}[&continuationToken={patContinuationToken}]
                    //       => 200 OK {
                    //                   "count":n,
                    //                   "value":[{
                    //                      "authorizationId":"{pat.AuthorizationId}",
                    //                        …
                    //                   },…],
                    //                   "continuationToken":"{patContinuationToken}"
                    //                 }
                    var pageOfPats = tokenAdminHttpClient.ListPersonalAccessTokensAsync(
                        userDescriptor, pageSize: patPageSize, continuationToken: patContinuationToken?.ToString()).SyncResult();

                    // In this case the continuation token (if any) is returned in the response body as shown above,
                    // which maps directly in C# to a property of the result object:
                    patContinuationToken = pageOfPats.ContinuationToken;

                    // Track the authorization IDs from the objects returned in the response:
                    authorizationIds.AddRange(pageOfPats.SessionTokens.Select(pat => pat.AuthorizationId));
                }
                // Repeat the above while we still have more pages of PATs:
                while (patContinuationToken != null && patContinuationToken != Guid.Empty);
            }

            return authorizationIds;
        }
        
        [ClientSampleMethod(TokenAdminResourceIds.AreaName, TokenAdminResourceIds.RevocationsResource)]
        public void RevokePersonalAccessTokensForSpecificUsersInYourOrganization()
        {
            // (5) REVOKING PERSONAL ACCESS TOKENS FOR SPECIFIC USERS
            // - Get the authorization IDs corresponding to those specific users' personal access tokens, as in either (4) or (1+4) above.
            // - For those specific authorization IDs, create and send the corresponding revocations, as in (7) below
            var authorizationIds = GetPersonalAccessTokenAuthorizationIdsForSpecificUsersInYourOrganization();
            RevokeAuthorizationsForASetOfUsers(authorizationIds);
        }

        [ClientSampleMethod(TokenAdminResourceIds.AreaName, TokenAdminResourceIds.RevocationsResource)]
        public void RevokePersonalAccessTokensForAllUsersInYourOrganization()
        {
            // (6) REVOKING PERSONAL ACCESS TOKENS FOR ALL USERS
            // - In pages, get the authorization IDs corresponding to the users' personal access tokens, as in (3+4) above.
            // - For each page of authorization IDs, create and send the corresponding page of revocations, as in (7) below.
            GetPersonalAccessTokenAuthorizationIdsForAllUsersInYourOrganization(thenForEachPage: RevokeAuthorizationsForASetOfUsers);
        }

        private void RevokeAuthorizationsForASetOfUsers(IEnumerable<Guid> authorizationIds)
        {
            var tokenAdminHttpClient = Context.Connection.GetClient<TokenAdminHttpClient>();

            // (7) REVOKING SPECIFIC AUTHORIZATIONS

            // Once you have the list of authorization IDs you want to revoke, 
            // you can use the following token administration endpoint to revoke them in batch, up to a server-side limit. 
            // Just as we've structured it here, you can revoke a list of authorizations that correspond to multiple users in one call,
            // but keep in mind that you will receive an error if you pass too many revocations at once.

            var revocations = authorizationIds.Select(id => new TokenAdminRevocation { AuthorizationId = id });

            // HTTP: POST /_apis/tokenAdmin/revocations
            //       [{
            //          "authorizationId":"{authorizationId}"
            //        },…]
            //       => 204 No Content
            tokenAdminHttpClient.RevokeAuthorizationsAsync(revocations).SyncResult();
        }

        [ClientSampleMethod(TokenAdminResourceIds.AreaName, TokenAdminResourceIds.RevocationRulesResource)]
        public void RevokeSelfDescribingSessionTokensForUsersInYourOrganization()
        {
            var tokenAdminHttpClient = Context.Connection.GetClient<TokenAdminHttpClient>();

            // (8) CREATING OAUTH REVOCATION RULES

            // Not all kinds of OAuth authorizations can be revoked directly.
            // Some, such as self-describing session tokens, must instead by revoked by creating a rule
            // which will be evaluated and used to reject matching OAuth credentials at authentication time.
            // Revocation rules as shown here will apply to all credentials that were issued before the supplied datetime
            // (or if omitted, at the time at which the rule is created), and which match a particular set of OAuth scopes (which must be provided).
            // For a list of all OAuth scopes supported by VSTS, see: 
            // https://docs.microsoft.com/en-us/vsts/integrate/get-started/authentication/oauth?view=vsts#scopes

            var revocationRule = new TokenAdminRevocationRule
            {
                Scopes = "vso.code vso.packaging", // rejects any token presented which matches EITHER the Code OR the Packaging scope
                CreatedBefore = DateTime.Now - TimeSpan.FromDays(1) // rejects any token that was created more than a day ago
            };

            // HTTP: POST /_apis/tokenAdmin/revocationRules
            //       {
            //         "scopes":"{scopeToRevoke}",
            //         "createdBefore":"{dateTimeToStopRevokingAt}"
            //       }
            //       => 204 No Content
            tokenAdminHttpClient.CreateRevocationRuleAsync(revocationRule).SyncResult();
        }
    }
}