using System;
using System.Threading.Tasks;
using Discord;
using Discord.Webhook;
using Discord.WebSocket;

using System.Configuration;
using System.Collections.Specialized;
using System.Collections.Generic;

namespace DiscordBridge
{
    class Program
    {
        private DiscordSocketClient _client;
        private DiscordWebhookClient _webhook;

        static Dictionary<string, string> Config = new Dictionary<string, string>();

        // Discord.Net heavily utilizes TAP for async, so we create
        // an asynchronous context from the beginning.
        static void Main(string[] args)
        {
            NameValueCollection APPSettings = ConfigurationManager.AppSettings;
            foreach (string ConfigKey in APPSettings)
            {
                Config.Add(ConfigKey, APPSettings.GetValues(ConfigKey)[0]);
            }
            // Start the discord ASYNC shit fest.
            new Program().MainAsync().GetAwaiter().GetResult();
        }

        public async Task MainAsync()
        {
            string WebHookURI = @"";
            _webhook = new DiscordWebhookClient(, WebHookURI);

            _webhook.Log += LogAsync;
            
            /*
            await _webhook.SendMessageAsync(
                "Test webhook message",
                false,
                null,
                @"TestUsername"
            );
            */

            _client = new DiscordSocketClient();
            _client.Log += LogAsync;
            _client.Ready += ReadyAsync;
            _client.MessageReceived += MessageReceivedAsync;

            // Tokens should be considered secret data, and never hard-coded.
            await _client.LoginAsync(TokenType.Bot, @"");
            await _client.StartAsync();

            // Block the program until it is closed.
            await Task.Delay(-1);
        }

        private Task LogAsync(LogMessage log)
        {
            Console.WriteLine(log.ToString());
            return Task.CompletedTask;
        }

        // The Ready event indicates that the client has opened a
        // connection and it is now safe to access the cache.
        private Task ReadyAsync()
        {
            Console.WriteLine($"{_client.CurrentUser} is connected!");

            return Task.CompletedTask;
        }

        // This is not the recommended way to write a bot - consider
        // reading over the Commands Framework sample.
        private async Task MessageReceivedAsync(SocketMessage message)
        {
            Console.WriteLine(message.Content);

            // The bot should never respond to itself.
            if (message.Author.Id == _client.CurrentUser.Id)
                return;

            if (message.Content == "!ping")
                await message.Channel.SendMessageAsync("pong!");
        }
    }
}