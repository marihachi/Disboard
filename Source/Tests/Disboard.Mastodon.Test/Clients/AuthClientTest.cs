﻿using System.Threading.Tasks;

using Disboard.Mastodon.Enums;
using Disboard.Test.Helpers;

using Xunit;

namespace Disboard.Mastodon.Test.Clients
{
    public class AuthClientTest : MastodonTestClient
    {
        [Fact]
        public async Task AccessTokenAsync()
        {
            var actual = await TestClient.Auth.AccessTokenAsync(Constants.RedirectUriForClient, "21238fbf78892764c7529ffbf8aecf8440bb2a09699919480941c0812e9c2709");
            actual.CheckRecursively();
        }

        [Fact]
        public void AuthorizeUrl()
        {
            const string clientId = "08b26d89391544984b9a23d95f3a4c91b2f7cdfff7775a7452f7c1cb6c6c048d";
            var query = $"scope=read%20write%20follow&response_type=code&redirect_uri=urn%3Aietf%3Awg%3Aoauth%3A2.0%3Aoob&client_id={clientId}";

            var scopes = AccessScope.Read | AccessScope.Write | AccessScope.Follow;
            var actual = TestClient.Auth.AuthorizeUrl(Constants.RedirectUriForClient, scopes);
            actual.Is($"https://mastodon.cloud/oauth/authorize?{query}");
        }
    }
}