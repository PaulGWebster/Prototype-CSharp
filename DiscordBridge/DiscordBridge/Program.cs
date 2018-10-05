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
        private DiscordSocketClient _discobot;
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
            bool ErrorCondition = false;
            if (
                Config.TryGetValue("WebHookToken",out string WebHookToken)
                && 
                Config.TryGetValue("WebHookID", out string WebHookID)
            )
            {
                try
                {
                    _webhook = new DiscordWebhookClient(Convert.ToUInt64(WebHookID), WebHookToken);
                }
                catch
                {
                    ErrorCondition = true;
                }
            }
            else
            {
                ErrorCondition = true;
            }
            
            if (ErrorCondition)
            {
                Console.WriteLine("Failure extracting WebHook token or id");
                Environment.Exit(1);
            }

            _webhook.Log += LogAsync;
            
            // Webhook configuration done, lets do the main bot

            if (Config.TryGetValue("DiscordToken", out string DiscordToken))
            {
                _discobot = new DiscordSocketClient();
                _discobot.Log += LogAsync;
                _discobot.Ready += ReadyAsync;
                _discobot.MessageReceived += MessageReceivedAsync;

                // Tokens should be considered secret data, and never hard-coded.
                await _discobot.LoginAsync(TokenType.Bot, DiscordToken);
            }
            else
            {
                Console.WriteLine("Failure extracting or registering with Bot Token");
                Environment.Exit(1);
            }

            // Both API's registered lets proceed.
            await _discobot.StartAsync();

            // Block the program until it is closed.
            await Task.Delay(-1);

            /*
            await _webhook.SendMessageAsync(
                "Test webhook message",
                false,
                null,
                @"TestUsername"
            );
            */
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
            Console.WriteLine($"{_discobot.CurrentUser} is connected!");

            return Task.CompletedTask;
        }

        // This is not the recommended way to write a bot - consider
        // reading over the Commands Framework sample.
        private async Task MessageReceivedAsync(SocketMessage message)
        {
            Console.WriteLine(message.Content);

            // The bot should never respond to itself.
            if (message.Author.Id == _discobot.CurrentUser.Id)
                return;

            if (message.Content == "!ping")
                await message.Channel.SendMessageAsync("pong!");
        }
    }
}