﻿using ESI.NET.Enumerations;
using ESI.NET.Logic;
using ESI.NET.Models.Character;
using ESI.NET.Models.SSO;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;


namespace ESI.NET
{
    public class SsoLogic
    {
        private readonly HttpClient _client;
        private readonly EsiConfig _config;
        private readonly string _clientKey;
        private readonly string _ssoUrl;

        public SsoLogic(HttpClient client, EsiConfig config)
        {
            _client = client;
            _config = config;
            switch (_config.DataSource)
            {
                case DataSource.Tranquility:
                    _ssoUrl = "https://login.eveonline.com";
                    break;
                case DataSource.Singularity:
                    _ssoUrl = "https://sisilogin.testeveonline.com";
                    break;
                case DataSource.Serenity:
                    _ssoUrl = "https://login.evepc.163.com";
                    break;
            }
            _clientKey = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{config.ClientId}:{config.SecretKey}"));
        }

        public string CreateAuthenticationUrl(List<string> scopes = null)
            => $"{_ssoUrl}/oauth/authorize/?response_type=code&redirect_uri={Uri.EscapeDataString(_config.CallbackUrl)}&client_id={_config.ClientId}{((scopes != null) ? $"&scope={string.Join(" ", scopes)}" : "")}";


 
        /// <summary>
        /// SSO Token helper
        /// </summary>
        /// <param name="clientId"></param>
        /// <param name="secretKey"></param>
        /// <param name="grantType"></param>
        /// <param name="code">The authorization_code or the refresh_token</param>
        /// <returns></returns>
        public async Task<SsoToken> GetToken(GrantType grantType, string code)
        {
            var body = $"grant_type={grantType.ToEsiValue()}";
            if (grantType == GrantType.AuthorizationCode)
                body += $"&code={code}";
            else if (grantType == GrantType.RefreshToken)
                body += $"&refresh_token={code}";

            HttpContent postBody = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _clientKey);

            var response = await _client.PostAsync($"{_ssoUrl}/oauth/token", postBody).Result.Content.ReadAsStringAsync();
            var token = JsonConvert.DeserializeObject<SsoToken>(response);

            return token;
        }


        /// <summary>
        /// Get authentication for the v2 Auth Flow
        /// </summary>
        /// <param name="scopes">ESI Scopes to request</param>
        /// <param name="codeVerifier">challenge code</param>
        /// <param name="state">unique state</param>
        /// <returns>URL for authentication</returns>
        public string CreateAuthenticationUrlV2(List<string> scopes, string codeVerifier, string state)
        {
            // create code_challenge
            var base64CodeVerifier = Convert.ToBase64String(Encoding.UTF8.GetBytes(codeVerifier)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var encodedCodeChallenge = string.Empty ;
            using (var sha256 = SHA256.Create())
            {
                var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(base64CodeVerifier));
                encodedCodeChallenge = Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }

            return $"https://login.eveonline.com/v2/oauth/authorize/?response_type=code&redirect_uri={Uri.EscapeDataString(_config.CallbackUrl)}&client_id={_config.ClientId}&code_challenge={encodedCodeChallenge}&code_challenge_method=S256&state={state}{((scopes != null) ? $"&scope={string.Join(" ", scopes)}" : "")}";
        }


        /// <summary>
        /// Get SSO Token for the v2 Auth flow
        /// </summary>
        public async Task<SsoToken> GetTokenV2(GrantType grantType, string code, string codeVerifier, List<string> scopes)
        {
            SsoToken ssoResult = new SsoToken();

            try
            {
                var body = $"grant_type={grantType.ToEsiValue()}";

                body += $"&client_id={_config.ClientId}";

                if (grantType == GrantType.AuthorizationCode)
                {
                    body += $"&code={code}";

                    var codeVerifierBytes = Encoding.ASCII.GetBytes(codeVerifier);
                    var base64CodeVerifierBytes = Convert.ToBase64String(codeVerifierBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    body += $"&code_verifier={base64CodeVerifierBytes}";

                }
                else if (grantType == GrantType.RefreshToken)
                {

                    body += $"&refresh_token={code}";

                    if (scopes != null)
                    {
                        body += $"&scope={string.Join(" ", scopes)}";
                    }
                }

                HttpContent postBody = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
                _client.DefaultRequestHeaders.Host = "login.eveonline.com";

                var response = await _client.PostAsync("https://login.eveonline.com/v2/oauth/token", postBody);
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    string resultstr = await response.Content.ReadAsStringAsync();

                    ssoResult = JsonConvert.DeserializeObject<SsoToken>(resultstr);
                }
            }
            catch
            {

            }

            return ssoResult;
        }


        /// <summary>
        /// Verifies the Character information for the provided Token information.
        /// While this method represents the oauth/verify request, in addition to the verified data that ESI returns, this object also stores the Token and Refresh token
        /// and this method also uses ESI retrieves other information pertinent to making calls in the ESI.NET API. (alliance_id, corporation_id, faction_id)
        /// You will need a record in your database that stores at least this information. Serialize and store this object for quick retrieval and token refreshing.
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public async Task<AuthorizedCharacterData> Verify(SsoToken token)
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            var response = await _client.GetAsync($"{_ssoUrl}/oauth/verify").Result.Content.ReadAsStringAsync();
            var authorizedCharacter = JsonConvert.DeserializeObject<AuthorizedCharacterData>(response);
            authorizedCharacter.Token = token.AccessToken;
            authorizedCharacter.RefreshToken = token.RefreshToken;

            var url = $"{_config.EsiUrl}v1/characters/affiliation/?datasource={_config.DataSource.ToEsiValue()}";
            var body = new StringContent(JsonConvert.SerializeObject(new int[] { authorizedCharacter.CharacterID }), Encoding.UTF8, "application/json");

            // Get more specifc details about authorized character to be used in API calls that require this data about the character
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            var characterResponse = await client.PostAsync(url, body).ConfigureAwait(false);

            //var characterResponse = new CharacterLogic(_client, _config, authorizedCharacter).Affiliation(new int[] { authorizedCharacter.CharacterID }).ConfigureAwait(false).GetAwaiter().GetResult();
            if (characterResponse.StatusCode == System.Net.HttpStatusCode.OK)
            {
                EsiResponse<List<Affiliation>> affiliations = new EsiResponse<List<Affiliation>>(characterResponse, "Post|/character/affiliations/", "v1");
                var characterData = affiliations.Data.First();

                authorizedCharacter.AllianceID = characterData.AllianceId;
                authorizedCharacter.CorporationID = characterData.CorporationId;
                authorizedCharacter.FactionID = characterData.FactionId;
            }

            return authorizedCharacter;
        }
    }
}
