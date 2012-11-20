using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using DotNetOpenAuth.Messaging;
using DotNetOpenAuth.OAuth;
using DotNetOpenAuth.OAuth.ChannelElements;
using Kudu.Client.Infrastructure;
using Kudu.Contracts.Dropbox;
using Kudu.FunctionalTests.Infrastructure;
using Kudu.TestHarness;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Kudu.FunctionalTests
{
    public class DropboxTests
    {
        static Random _random = new Random(unchecked((int)DateTime.Now.Ticks));

        [Fact]
        public void TestDropboxBasic()
        {
            OAuthInfo oauth = GetOAuthInfo();
            if (oauth == null)
            {
                // only run in private kudu
                return;
            }

            AccountInfo account = GetAccountInfo(oauth);
            DropboxDeployInfo deploy = GetDeployInfo(oauth);
            string appName = KuduUtils.GetRandomWebsiteName("DropboxTest");
            ApplicationManager.Run(appName, appManager =>
            {
                appManager.SettingsManager.SetValues(
                    new KeyValuePair<string, string>("dropbox_username", account.display_name),
                    new KeyValuePair<string, string>("dropbox_email", account.email)
                    );

                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(appManager.ServiceUrl);
                client.DefaultRequestHeaders.Add("user-agent", "dropbox");
                if (appManager.DeploymentManager.Credentials != null)
                {
                    client.SetClientCredentials(appManager.DeploymentManager.Credentials); 
                }
                client.PostAsJsonAsync("deploy", deploy).Result.EnsureSuccessStatusCode();

                KuduAssert.VerifyUrl(appManager.SiteUrl + "/default.html", "Hello Default!");
                KuduAssert.VerifyUrl(appManager.SiteUrl + "/temp/temp.html", "Hello Temp!");
            });
        }

        private OAuthInfo GetOAuthInfo()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (var reader = new JsonTextReader(new StreamReader(assembly.GetManifestResourceStream("Kudu.FunctionalTests.dropbox.oauth.json"))))
            {
                JsonSerializer serializer = new JsonSerializer();
                return serializer.Deserialize<OAuthInfo>(reader);
            }
        }

        private AccountInfo GetAccountInfo(OAuthInfo oauth)
        {
            var consumer = CreateWebConsumer(oauth);
            var endpoint = new MessageReceivingEndpoint("https://api.dropbox.com/1/account/info", HttpDeliveryMethods.AuthorizationHeaderRequest | HttpDeliveryMethods.GetRequest);
            var request = consumer.PrepareAuthorizedRequest(endpoint, oauth.Token);
            request.Accept = "application/json";
            var response = (HttpWebResponse)request.GetResponse();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception("Fail to retrive account info.  status=" + response.StatusCode);
            }

            using (var reader = new JsonTextReader(new StreamReader(response.GetResponseStream())))
            {
                JsonSerializer serializer = new JsonSerializer();
                return serializer.Deserialize<AccountInfo>(reader);
            }
        }

        private DeltaInfo GetDeltaInfo(OAuthInfo oauth, string cursor = null)
        {
            var url = new StringBuilder("https://api.dropbox.com/1/delta");
            if (!String.IsNullOrEmpty(cursor))
            {
                url.Append("/?cursor=");
                url.Append(cursor);
            }
            var consumer = CreateWebConsumer(oauth);
            var endpoint = new MessageReceivingEndpoint(url.ToString(), HttpDeliveryMethods.AuthorizationHeaderRequest | HttpDeliveryMethods.PostRequest);
            var request = consumer.PrepareAuthorizedRequest(endpoint, oauth.Token);
            request.Accept = "application/json";
            var response = (HttpWebResponse)request.GetResponse();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception("Fail to retrive delta info.  status=" + response.StatusCode);
            }

            using (var reader = new JsonTextReader(new StreamReader(response.GetResponseStream())))
            {
                JsonSerializer serializer = new JsonSerializer();
                return new DeltaInfo(serializer.Deserialize<JObject>(reader));
            }
        }

        private WebConsumer CreateWebConsumer(OAuthInfo oauth)
        {
            var tokenManager = new ConsumerTokenManager(oauth);
            var requestTokenEndpoint = new MessageReceivingEndpoint(
                new Uri("https://api.dropbox.com/1/oauth/request_token"),
                HttpDeliveryMethods.PostRequest);
            var userAuthorizationEndpoint = new MessageReceivingEndpoint(
                new Uri("https://www.dropbox.com/1/oauth/authorize"),
                HttpDeliveryMethods.PostRequest);
            var accessTokenEndpoint = new MessageReceivingEndpoint(
                new Uri("https://api.dropbox.com/1/oauth/access_token"),
                HttpDeliveryMethods.PostRequest);
            WebConsumer consumer = new WebConsumer(
                new ServiceProviderDescription
                {
                    RequestTokenEndpoint = requestTokenEndpoint,
                    UserAuthorizationEndpoint = userAuthorizationEndpoint,
                    AccessTokenEndpoint = accessTokenEndpoint,
                    ProtocolVersion = ProtocolVersion.V10a,
                    TamperProtectionElements = new ITamperProtectionChannelBindingElement[] { new HmacSha1SigningBindingElement() }
                },
                tokenManager);

            return consumer;
        }

        private DropboxDeployInfo GetDeployInfo(OAuthInfo oauth, string cursor = null)
        {
            List<DropboxDeltaInfo> deltas = new List<DropboxDeltaInfo>();
            string timeStamp = GetUtcTimeStamp();
            string oldCursor = cursor;
            string newCursor = "";
            while (true)
            {
                DeltaInfo delta = GetDeltaInfo(oauth, cursor);
                newCursor = delta.cursor;
                if (newCursor == oldCursor)
                {
                    break;
                }

                foreach (EntryInfo info in delta.entries)
                {
                    DropboxDeltaInfo item = new DropboxDeltaInfo { Path = info.path };
                    if (info.metadata == null || info.metadata.is_deleted || string.IsNullOrEmpty(info.metadata.path))
                    {
                        item.IsDeleted = true;
                    }
                    else
                    {
                        item.IsDirectory = info.metadata.is_dir;
                        if (!item.IsDirectory)
                        {
                            item.Nonce = GetNonce();
                            item.Signature = GetSignature(oauth, info.path, timeStamp, item.Nonce);
                        }
                    }

                    deltas.Add(item);
                }

                if (!delta.has_more)
                {
                    break;
                }
            }

            if (deltas.Count == 0)
            {
                throw new InvalidOperationException("the repo is up-to-date.");
            }

            return new DropboxDeployInfo
            {
                TimeStamp = timeStamp,
                Token = oauth.Token,
                ConsumerKey = oauth.ConsumerKey,
                OAuthVersion = "1.0",
                SignatureMethod = "HMAC-SHA1",
                OldCursor = oldCursor,
                NewCursor = newCursor,
                Path = "/",
                Deltas = deltas
            };
        }

        private string GetSignature(OAuthInfo oauth, string path, string timeStamp, string nonce)
        {
            var strb = new StringBuilder();
            strb.AppendFormat("{0}={1}", "oauth_consumer_key", oauth.ConsumerKey);
            strb.AppendFormat("&{0}={1}", "oauth_nonce", nonce);
            strb.AppendFormat("&{0}={1}", "oauth_signature_method", "HMAC-SHA1");
            strb.AppendFormat("&{0}={1}", "oauth_timestamp", timeStamp);
            strb.AppendFormat("&{0}={1}", "oauth_token", oauth.Token);
            strb.AppendFormat("&{0}={1}", "oauth_version", "1.0");

            string data = String.Format("{0}&{1}&{2}",
                "GET",
                UrlEncode("https://api-content.dropbox.com/1/files/sandbox" + path),
                UrlEncode(strb.ToString()));

            var key = String.Format("{0}&{1}",
                UrlEncode(oauth.ConsumerSecret),
                UrlEncode(oauth.TokenSecret));

            HMACSHA1 hmacSha1 = new HMACSHA1();
            hmacSha1.Key = Encoding.ASCII.GetBytes(key);
            byte[] hashBytes = hmacSha1.ComputeHash(Encoding.ASCII.GetBytes(data));
            return Convert.ToBase64String(hashBytes);
        }

        private string UrlEncode(string str)
        {
            Regex reg = new Regex("%[a-f0-9]{2}");
            return reg.Replace(HttpUtility.UrlEncode(str), m => m.Value.ToUpperInvariant());
        }

        private string GetUtcTimeStamp()
        {
            TimeSpan ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            return Convert.ToInt64(ts.TotalSeconds).ToString();
        }

        private string GetNonce()
        {
            const string unreserved = "-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";

            var chars = new char[8];
            for (int i = 0; i < 8; ++i)
            {
                chars[i] = unreserved[_random.Next(unreserved.Length)];
            }
            return new String(chars);
        }

        public class ConsumerTokenManager : IConsumerTokenManager
        {
            private OAuthInfo _oauth;

            public ConsumerTokenManager(OAuthInfo oauth)
            {
                _oauth = oauth;
            }

            public string ConsumerKey
            {
                get { return _oauth.ConsumerKey; }
            }

            public string ConsumerSecret
            {
                get { return _oauth.ConsumerSecret; }
            }

            public void ExpireRequestTokenAndStoreNewAccessToken(string consumerKey, string requestToken, string accessToken, string accessTokenSecret)
            {
                throw new NotImplementedException();
            }

            public string GetTokenSecret(string token)
            {
                if (token == _oauth.ConsumerKey)
                {
                    return _oauth.ConsumerSecret;
                }
                else if (token == _oauth.Token)
                {
                    return _oauth.TokenSecret;
                }

                throw new InvalidOperationException("No token secret for " + token);
            }

            public TokenType GetTokenType(string token)
            {
                throw new NotImplementedException();
            }

            public void StoreNewRequestToken(DotNetOpenAuth.OAuth.Messages.UnauthorizedTokenRequest request, DotNetOpenAuth.OAuth.Messages.ITokenSecretContainingMessage response)
            {
                throw new NotImplementedException();
            }
        }

        public class OAuthInfo
        {
            public string ConsumerKey { get; set; }
            public string ConsumerSecret { get; set; }
            public string Token { get; set; }
            public string TokenSecret { get; set; }
        }

        public class AccountInfo
        {
            public string display_name { get; set; }
            public string email { get; set; }
        }

        public class DeltaInfo
        {
            public DeltaInfo(JObject json)
            {
                cursor = (string)json["cursor"];
                has_more = (bool)json["has_more"];
                entries = new List<EntryInfo>();
                foreach (JArray entry in json["entries"])
                {
                    entries.Add(new EntryInfo(entry));
                }
            }

            public string cursor { get; set; }
            public bool has_more { get; set; }
            public List<EntryInfo> entries { get; set; }
        }

        public class EntryInfo
        {
            public EntryInfo(JArray json)
            {
                path = (string)json[0];
                metadata = json[1] is JObject ? new Metadata((JObject)json[1]) : null;
            }

            public string path { get; set; }
            public Metadata metadata { get; set; }
        }

        public class Metadata
        {
            public Metadata(JObject json)
            {
                path = (string)json["path"];
                is_dir = json["is_dir"] == null ? false : (bool)json["is_dir"];
                is_deleted = json["is_deleted"] == null ? false : (bool)json["is_deleted"];
            }

            public string path { get; set; }
            public bool is_dir { get; set; }
            public bool is_deleted { get; set; }
        }
    }
}

