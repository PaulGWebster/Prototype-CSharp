using System;
using System.Threading.Tasks;

using Discord;
using Discord.Webhook;
using Discord.WebSocket;

using System.Configuration;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Threading;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.IO;
using System.Net;

namespace DiscordBridge
{
    class Program
    {
        // Global config
        static Dictionary<string, string> Config = new Dictionary<string, string>();

        // Registry for keeping associations via nickname changes
        static Dictionary<string, UserProfile> Registry = new Dictionary<string, UserProfile>();

        // Static queues to and from discord
        static BlockingCollection<DataPacket> BufferFromDiscord = new BlockingCollection<DataPacket>();
        static BlockingCollection<DataPacket> BufferToDiscord = new BlockingCollection<DataPacket>();

        // A special place for the IRCMaster
        static Thread IRCMaster = null;
        //static Dictionary<string, Thread> Threads = new Dictionary<string, Thread>();

        // Keep the connections to discord private
        private DiscordSocketClient _discobot;
        private DiscordWebhookClient _webhook;

        // Discord.Net heavily utilizes TAP for async, so we create
        // an asynchronous context from the beginning.
        static void Main(string[] args)
        {
            NameValueCollection APPSettings = ConfigurationManager.AppSettings;
            foreach (string ConfigKey in APPSettings)
            {
                Config.Add(ConfigKey, APPSettings.GetValues(ConfigKey)[0]);
            }

            // Start a control thread for handling the IRC side of things
            IRCMaster = new Thread(new ThreadStart(Governor));
            IRCMaster.Start();

            // Start the discord ASYNC shit fest.
            new Program().MainAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        ///  IRC Controller
        /// </summary>
        /*
            while (true)
            {
                DataPacket DBlock = BufferFromDiscord.Take();
                //BufferToDiscord.Add(DBlock);
                Console.WriteLine("Message: {0}", DBlock.Message);
            }
        */

        private static void Governor()
        {
            TcpClient IRCConnection = new TcpClient();
            NetworkStream IOStream = null;
            StreamReader I_Stream = null;
            StreamWriter _OStream = null; 
            IRCState CState = new IRCState();
            List<String> ReadBuffer = new List<String>();

            while (true)
            {
                // Force clear the IRC Recv buffer
                ReadBuffer = new List<String>();

                // this block just greedily processes all message received from irc, it will always take around 5ms.
                // we are using this as a cpu brake
                if (IRCConnection.Connected)
                {
                    // Read all messages first - this will always fail
                    I_Stream.BaseStream.ReadTimeout = 5;
                    try
                    {
                        while (true)
                        {
                            ReadBuffer.Add(I_Stream.ReadLine());
                        }
                    }
                    catch
                    {
                        // Reset the standard timeout
                        I_Stream.BaseStream.ReadTimeout = 10;
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }

                // Handle any pending inbound IRC messages
                foreach (string IRCEvent in ReadBuffer)
                {
                    Console.WriteLine(IRCEvent);
                    string[] IRCRead = IRCEvent.Split(' ');
                    switch (IRCRead.Length)
                    {
                        case 2:
                            if (IRCRead[0].Equals("PING"))
                            {
                                _OStream.WriteLine("PONG " + IRCRead[1]);
                                _OStream.Flush();
                            }
                            break;
                        default:
                            break;
                    }
                }

                if (!CState.Connected)
                {
                    // First of all check our IRC state if we are not connecting or connected we should be!
                    if (!CState.Connecting)
                    {
                        if (IRCConnection.Connected)
                        {
                            IRCConnection.Close();
                        }
                        Console.WriteLine("Connecting to IRC");
                        IRCConnection = new TcpClient();
                        IRCConnection.Connect(@"irc.0x00sec.org", 6667);
                        IOStream = IRCConnection.GetStream();
                        I_Stream = new StreamReader(IOStream);
                        _OStream = new StreamWriter(IOStream);
                        CState = new IRCState
                        {
                            Connecting = true
                        };
                    }
                    else if (CState.Connecting && !CState.AuthSend)
                    {
                        // We have not sent authoritive info yet, lets do it!
                        Console.WriteLine("Sending auth details");
                        _OStream.WriteLine(@"USER DiscordB DiscordB DiscordB :DiscordBirdge (nugget)");
                        _OStream.WriteLine(@"NICK DickSword");
                        _OStream.Flush();
                        CState.AuthSend = true;
                        CState.AuthSent = DateTime.UtcNow;
                    }
                    else if (CState.Connecting && CState.AuthSend && (DateTime.UtcNow.Subtract(CState.AuthSent).Seconds > 10))
                    {
                        // It took longer than 10 seconds and we have not heard jack
                        // Force the connection to reset next iteration
                        CState.Connecting = false;
                        CState.Connected = false;
                        CState.AuthSend = false;
                        Console.WriteLine("Connection attempt reset.");
                    }
                }
            }
        }

        /// <summary>
        /// Discord related async functions
        /// </summary>

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
            
            // Webhook configuration done, lets do the main botDiscordUID

            if (Config.TryGetValue("DiscordToken", out string DiscordToken))
            {
                _discobot = new DiscordSocketClient();
                _discobot.Log += LogAsync;
                _discobot.Ready += ReadyAsync;
                _discobot.MessageReceived += MessageReceivedAsync;
                _discobot.GuildMemberUpdated += UserNickChanged;

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

            // Sit waiting for things ToDiscord
            bool Shutdown = false;
            while(!Shutdown)
            {
                DataPacket DataPkt = BufferToDiscord.Take();
                await _webhook.SendMessageAsync(
                    DataPkt.Message,
                    false,
                    null,
                    DataPkt.Identifier
                );
            }

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
            Console.WriteLine($"{_discobot.CurrentUser} is connected!");

            //'Discord.CollectionWrapper`1[Discord.WebSocket.SocketGuild]' to type 'Discord.WebSocket.SocketGuild'.'

            if (_discobot.Guilds.Count > 1)
            {
                throw new Exception("This bot can only mirror a single irc channel to a single discord guild(channel)");
            }

            foreach (SocketGuild Channel in _discobot.Guilds)
            {
                foreach (SocketGuildUser DiscordUser in Channel.Users)
                {
                    Registry.Add(
                        "discord:" + DiscordUser.Id,
                        new UserProfile
                        {
                            DiscordUsername = DiscordUser.Username,
                            DiscordNickname = DiscordUser.Nickname,
                            DiscordUID = DiscordUser.Id
                        }
                    );
                }
            }

            return Task.CompletedTask;
        }

        private async Task MessageReceivedAsync(SocketMessage message)
        {
            Console.WriteLine("Our id: {0}", _discobot.CurrentUser.Id);
            // The bot should never respond to itself.
            if (message.Author.Id == _discobot.CurrentUser.Id)
                return;

            BufferFromDiscord.Add(
                new DataPacket
                {
                    IsDiscord = true,
                    Identifier = message.Author.Id.ToString(),
                    Message = message.Content
                }
            );

            Console.WriteLine("Size of queue: {0}", BufferFromDiscord.Count);

            if (message.Content == "!ping")
                await message.Channel.SendMessageAsync("pong!");
        }

        public async Task UserNickChanged(SocketGuildUser before, SocketGuildUser after)
        {
            string Username = "discord:" + before.Id;

            lock (Registry)
            {
                if (Registry.TryGetValue(Username, out UserProfile User))
                {
                    User.DiscordNickname = after.Nickname;
                    User.DiscordUsername = after.Username;
                    Registry.Remove(Username);
                    Registry.TryAdd(Username, User);
                }
            }

            await Task.Yield();
        }
    }

    internal class IRCState
    {
        public IRCState()
        {

        }
        public bool Connecting { get; internal set; }
        public bool Connected { get; internal set; }
        public bool AuthSend { get; internal set; }
        public string Expect { get; internal set; }
        public DateTime AuthSent { get; internal set; }
    }

    internal class UserProfile
    {
        public string IRCNickname { get; internal set; }
        public string IRCUsername { get; internal set; }
        public bool IRCIdentified { get; internal set; }
        public bool IRCConnected { get; internal set; }
        public UInt64 DiscordUID { get; internal set; }
        public string DiscordUsername { get; internal set; }
        public string DiscordNickname { get; internal set; }
    }

    internal class DataPacket
    {
        public string Identifier { get; internal set; }
        public bool IsDiscord { get; internal set; }
        public string Message { get; internal set; }
    }
}