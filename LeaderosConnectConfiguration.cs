using Rocket.API;

namespace LeaderosConnect
{
    public class LeaderosConnectConfiguration : IRocketPluginConfiguration
    {
        public string WebsiteUrl { get; set; }
        public string ApiKey { get; set; }
        public string ServerToken { get; set; }
        public bool DebugMode { get; set; }
        public bool CheckPlayerOnline { get; set; }

        public void LoadDefaults()
        {
            WebsiteUrl = "https://yourwebsite.com";
            ApiKey = "YOUR_API_KEY_HERE";
            ServerToken = "YOUR_SERVER_TOKEN_HERE";
            DebugMode = false;
            CheckPlayerOnline = true;
        }
    }
}