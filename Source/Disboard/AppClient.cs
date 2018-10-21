﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Disboard.Exceptions;
using Disboard.Extensions;
using Disboard.Models;
using Disboard.Utils;

using Newtonsoft.Json;

// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedParameter.Local
// ReSharper disable PossibleMultipleEnumeration

namespace Disboard
{
    /// <summary>
    ///     Client Base Implementation
    /// </summary>
    public class AppClient
    {
        public delegate void CustomAuthFunc(HttpClient client, string url, ref IEnumerable<KeyValuePair<string, object>> parameters);

        private readonly AuthMode _authMode;
        private readonly string _baseUrl;
        private readonly HttpClient _httpClient;
        private readonly RequestMode _requestMode;
        private CustomAuthFunc _customAuth;
        public static string Version => "1.0";

        /// <summary>
        ///     Keys that send as binary data.
        /// </summary>
        protected List<string> BinaryParameters { get; set; } = new List<string>();

        #region Other Properties

        public string Domain { get; }

        #endregion

        /// <summary>
        ///     Constructor
        /// </summary>
        /// <param name="domain">Domain name</param>
        /// <param name="authMode">Authentication mode</param>
        /// <param name="requestMode">Serialization mode</param>
        protected AppClient(string domain, AuthMode authMode, RequestMode requestMode)
        {
            Domain = domain;
            _baseUrl = $"https://{domain}";
            _authMode = authMode;
            _requestMode = requestMode;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", $"Disboard/{Version}");
        }

        protected void RegisterCustomAuthenticator(CustomAuthFunc action)
        {
            _customAuth = action;
        }

        private void PrepareForAuthenticate(HttpMethod method, string url, ref IEnumerable<KeyValuePair<string, object>> parameters)
        {
            // ReSharper disable NotResolvedInText
            switch (_authMode)
            {
                case AuthMode.OAuth1:
                    PrepareForOAuth1A(method, url, parameters);
                    break;

                case AuthMode.OAuth2:
                    PrepareForOAuth2();
                    break;

                case AuthMode.Myself:
                    _customAuth?.Invoke(_httpClient, url, ref parameters);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(AuthMode), _authMode, null);
            }

            // ReSharper restore NotResolvedInText
        }

        private void PrepareForOAuth1A(HttpMethod method, string url, IEnumerable<KeyValuePair<string, object>> parameters)
        {
            var dictionary = new SortedDictionary<string, string>
            {
                ["oauth_consumer_key"] = ConsumerKey,
                ["oauth_nonce"] = GenerateNonce(),
                ["oauth_signature_method"] = "HMAC-SHA1",
                ["oauth_timestamp"] = GenerateTimestamp(),
                ["oauth_version"] = "1.0"
            };
            if (!string.IsNullOrWhiteSpace(AccessToken))
                dictionary["oauth_token"] = AccessToken;

            if (parameters != null)
                foreach (var parameter in parameters)
                    dictionary[parameter.Key] = parameter.Value.ToString();

            var key = $"{UrlEncode(ConsumerSecret)}&{UrlEncode(string.IsNullOrWhiteSpace(AccessTokenSecret) ? "" : AccessTokenSecret)}";
            var message = $"{method.Method}&{UrlEncode(url)}&{UrlEncode(string.Join("&", AsUrlParameter(dictionary)))}";

            var hmacsha1 = new HMACSHA1 {Key = Encoding.ASCII.GetBytes(key)};
            var signature = Convert.ToBase64String(hmacsha1.ComputeHash(Encoding.ASCII.GetBytes(message)));
            var header = string.Join(",", AsUrlParameter(dictionary.Where(w => w.Key.StartsWith("oauth_"))));

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", $"{header},oauth_signature={UrlEncode(signature)}");
        }

        private void PrepareForOAuth2()
        {
            if (!string.IsNullOrWhiteSpace(AccessToken))
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        private static string GenerateTimestamp()
        {
            return Convert.ToInt64((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0)).TotalSeconds).ToString();
        }

        private static string GenerateNonce()
        {
            const string letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var sb = new StringBuilder();
            var random = new Random();
            for (var i = 0; i < 32; i++)
                sb.Append(letters[random.Next(letters.Length)]);
            return sb.ToString();
        }

        private void ProcessLinkHeader(HttpResponseMessage response, object obj)
        {
            if (!response.Headers.Contains("link"))
                return;
            if (!(obj is IPagenator pagenator))
                return;

            var links = SimpleWebLinkParser.Parse(response.Headers.GetValues("link").FirstOrDefault());
            pagenator.Client = this;
            pagenator.First = links.FirstOrDefault(w => w.Rel == "first")?.Uri;
            pagenator.Next = links.FirstOrDefault(w => w.Rel == "next")?.Uri;
            pagenator.Prev = links.FirstOrDefault(w => w.Rel == "prev")?.Uri;
            pagenator.Last = links.FirstOrDefault(w => w.Rel == "last")?.Uri;
        }

        #region Utilities

        public static IEnumerable<string> AsUrlParameter<T>(IEnumerable<KeyValuePair<string, T>> parameters)
        {
            return parameters.Select(w => $"{w.Key}={UrlEncode(w.Value.ToString())}");
        }

        public static string UrlEncode(string str)
        {
            const string reservedLetters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~";
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(str))
                if (reservedLetters.Contains(((char) b).ToString()))
                    sb.Append((char) b);
                else
                    sb.Append($"%{b:X2}");
            return sb.ToString();
        }

        private static byte[] ReadAsByteArray(Stream stream)
        {
            var buffer = new byte[16 * 1024];
            using (var ms = new MemoryStream())
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    ms.Write(buffer, 0, read);
                return ms.ToArray();
            }
        }

        #endregion

        #region HTTP Request

        /// <summary>
        ///     Send a GET request to endpoint with parameters.
        /// </summary>
        /// <typeparam name="T">JSON deserialized class.</typeparam>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response deserialized to T</returns>
        public async Task<T> GetAsync<T>(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            var response = await GetAsyncInternal(endpoint, parameters).Stay();
            var obj = JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync().Stay());
            ProcessLinkHeader(response, obj);

            return obj;
        }

        /// <summary>
        ///     Send a GET request to endpoint with parameters.
        /// </summary>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response</returns>
        public async Task<string> GetAsync(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            var response = await GetAsyncInternal(endpoint, parameters).Stay();
            return await response.Content.ReadAsStringAsync().Stay();
        }

        private async Task<HttpResponseMessage> GetAsyncInternal(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            PrepareForAuthenticate(HttpMethod.Get, _baseUrl + endpoint, ref parameters);
            if (parameters != null && parameters.Any())
                endpoint += $"?{string.Join("&", AsUrlParameter(parameters))}";

            var response = await _httpClient.GetAsync(_baseUrl + endpoint).Stay();
            if (response.IsSuccessStatusCode)
                return response;
            throw await DisboardException.Create(response, _baseUrl + endpoint);
        }

        /// <summary>
        ///     Send a GET request to endpoint with parameters.
        /// </summary>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response (Stream)</returns>
        public async Task<Stream> GetStreamAsync(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            PrepareForAuthenticate(HttpMethod.Get, _baseUrl + endpoint, ref parameters);
            if (parameters != null && parameters.Any())
                endpoint += $"?{string.Join("&", AsUrlParameter(parameters))}";

            return await _httpClient.GetStreamAsync(endpoint).Stay();
        }

        /// <summary>
        ///     Send a POST request to endpoint with parameters.
        /// </summary>
        /// <typeparam name="T">JSON deserialized class.</typeparam>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response deserialized to T</returns>
        public async Task<T> PostAsync<T>(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            return await SendAsync<T>(HttpMethod.Post, endpoint, parameters).Stay();
        }

        /// <summary>
        ///     Send a POST request to endpoint with parameters.
        /// </summary>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response</returns>
        public async Task<string> PostAsync(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            return await SendAsync(HttpMethod.Post, endpoint, parameters).Stay();
        }

        /// <summary>
        ///     Send a PUT request to endpoint with parameters.
        /// </summary>
        /// <typeparam name="T">JSON deserialized class.</typeparam>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response deserialized to T</returns>
        public async Task<T> PutAsync<T>(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            return await SendAsync<T>(HttpMethod.Put, endpoint, parameters).Stay();
        }

        /// <summary>
        ///     Send a PUT request to endpoint with parameters.
        /// </summary>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response</returns>
        public async Task<string> PutAsync(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            return await SendAsync(HttpMethod.Put, endpoint, parameters);
        }

        /// <summary>
        ///     Send a DELETE request to endpoint with parameters.
        /// </summary>
        /// <typeparam name="T">JSON deserialized class.</typeparam>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response deserialized to T</returns>
        public async Task<T> DeleteAsync<T>(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            return JsonConvert.DeserializeObject<T>(await DeleteAsync(endpoint, parameters).Stay());
        }

        /// <summary>
        ///     Send a DELETE request to endpoint with parameters.
        /// </summary>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response</returns>
        public async Task<string> DeleteAsync(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            PrepareForAuthenticate(HttpMethod.Delete, _baseUrl + endpoint, ref parameters);
            if (parameters != null && parameters.Any())
                endpoint += $"?{string.Join("&", AsUrlParameter(parameters))}";

            var response = await _httpClient.DeleteAsync(_baseUrl + endpoint).Stay();
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync().Stay();
            throw await DisboardException.Create(response, _baseUrl + endpoint);
        }

        /// <summary>
        ///     Send a PATCH request to endpoint with parameters.
        /// </summary>
        /// <typeparam name="T">JSON deserialized class.</typeparam>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response deserialized to T</returns>
        public async Task<T> PatchAsync<T>(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            return await SendAsync<T>(new HttpMethod("PATCH"), endpoint, parameters).Stay();
        }

        /// <summary>
        ///     Send a PATCH request to endpoint with parameters.
        /// </summary>
        /// <param name="endpoint">Endpoint of API without base url.</param>
        /// <param name="parameters">Parameters</param>
        /// <returns>API response</returns>
        public async Task<string> PatchAsync(string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            return await SendAsync(new HttpMethod("PATCH"), endpoint, parameters).Stay();
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            var response = await SendAsyncInternal(method, endpoint, parameters).Stay();
            var obj = JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync().Stay());

            ProcessLinkHeader(response, obj);
            return obj;
        }

        private async Task<string> SendAsync(HttpMethod method, string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            var response = await SendAsyncInternal(method, endpoint, parameters).Stay();
            return await response.Content.ReadAsStringAsync().Stay();
        }

        private async Task<HttpResponseMessage> SendAsyncInternal(HttpMethod method, string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            switch (_requestMode)
            {
                case RequestMode.FormUrlEncoded:
                    return await SendAsFormDataAsync(method, endpoint, parameters).Stay();

                case RequestMode.Json:
                    return await SendAsJsonAsync(method, endpoint, parameters).Stay();

                default:
                    throw new ArgumentOutOfRangeException(nameof(RequestMode), _requestMode, null);
            }
        }

        private async Task<HttpResponseMessage> SendAsFormDataAsync(HttpMethod method, string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            HttpContent content;
            if (parameters != null && parameters.Any(w => BinaryParameters.Contains(w.Key)))
            {
                PrepareForAuthenticate(method, _baseUrl + endpoint, ref parameters);
                content = new MultipartFormDataContent();

                foreach (var parameter in parameters)
                {
                    HttpContent formDataContent;
                    if (BinaryParameters.Contains(parameter.Key))
                    {
                        using (var stream = new FileStream(parameter.Value.ToString(), FileMode.Open))
                            formDataContent = new ByteArrayContent(ReadAsByteArray(stream));
                        formDataContent.Headers.Add("Content-Disposition", $"form-data; name=\"{parameter.Key}\"; filename=\"{Path.GetFileName(parameter.Value.ToString())}\"");
                    }
                    else
                    {
                        var value = parameter.Value.ToString();
                        if (parameter.Value is bool)
                            value = value.ToLower();
                        formDataContent = new StringContent(value);
                    }
                    ((MultipartFormDataContent) content).Add(formDataContent, parameter.Key);
                }
            }
            else
            {
                PrepareForAuthenticate(method, _baseUrl + endpoint, ref parameters);
                var kvpCollection = parameters?.Select(w => new KeyValuePair<string, string>(w.Key, w.Value.ToString()));
                content = new FormUrlEncodedContent(kvpCollection);
            }

            var response = await _httpClient.SendAsync(new HttpRequestMessage(method, _baseUrl + endpoint) {Content = content}).Stay();
            if (response.IsSuccessStatusCode)
                return response;
            throw await DisboardException.Create(response, _baseUrl + endpoint);
        }

        private async Task<HttpResponseMessage> SendAsJsonAsync(HttpMethod method, string endpoint, IEnumerable<KeyValuePair<string, object>> parameters = null)
        {
            PrepareForAuthenticate(method, _baseUrl + endpoint, ref parameters);
            var dict = new Dictionary<string, object>();
            if (parameters != null)
                if (parameters.Any(w => BinaryParameters.Contains(w.Key)))
                    return await SendAsFormDataAsync(method, endpoint, parameters).Stay();
                else
                    foreach (var kvp in parameters)
                    {
                        if (dict.ContainsKey(kvp.Key))
                            throw new InvalidOperationException();
                        dict.Add(kvp.Key, kvp.Value);
                    }
            var body = JsonConvert.SerializeObject(dict);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(new HttpRequestMessage(method, _baseUrl + endpoint) {Content = content}).Stay();
            if (response.IsSuccessStatusCode)
                return response;
            throw await DisboardException.Create(response, _baseUrl + endpoint);
        }

        #endregion

        #region Application Keys

        /// <summary>
        ///     Client ID
        /// </summary>
        public string ClientId { get; set; }

        /// <summary>
        ///     Client Secret
        /// </summary>
        public string ClientSecret { get; set; }

        /// <summary>
        ///     Consumer Key (alias of <see cref="ClientId" />)
        /// </summary>
        public string ConsumerKey
        {
            get => ClientId;
            set => ClientId = value;
        }

        /// <summary>
        ///     Consumer secret (alias of <see cref="ClientSecret" />)
        /// </summary>
        public string ConsumerSecret
        {
            get => ClientSecret;
            set => ClientSecret = value;
        }

        #endregion

        #region User Keys

        /// <summary>
        ///     Access Token
        /// </summary>
        public string AccessToken { get; set; }

        /// <summary>
        ///     Access Token Secret
        /// </summary>
        public string AccessTokenSecret { get; set; }

        /// <summary>
        ///     Refresh Token (OAuth 2.0)
        /// </summary>
        public string RefreshToken { get; set; }

        #endregion
    }
}
