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
        static Dictionary<string, DiscordProfile> Registry = new Dictionary<string, DiscordProfile>();
        static Dictionary<string, IRCConnection> IRCConnections = new Dictionary<string, IRCConnection>();

        // Static queues to and from discord
        static BlockingCollection<DataPacket> BufferFromDiscord = new BlockingCollection<DataPacket>();
        static BlockingCollection<DataPacket> BufferToDiscord = new BlockingCollection<DataPacket>();

        // A special place for the IRCMaster
        static Thread IRCMaster = null;
        static Thread IRCConManager = null;
        static Thread IRCConProcessor = null;
        
        // Keep the connections to discord private
        private DiscordSocketClient _discobot;
        private DiscordWebhookClient _webhook;

        // The webhook ID is reffered to alot so lets make it static
        static UInt64 WebHookID = 0;

        // Discord.Net heavily utilizes TAP for async, so we create
        // an asynchronous context from the beginning.
        static void Main(string[] args)
        {
            NameValueCollection APPSettings = ConfigurationManager.AppSettings;
            foreach (string ConfigKey in APPSettings)
            {
                Config.Add(ConfigKey, APPSettings.GetValues(ConfigKey)[0]);
                if (ConfigKey.Equals("WebHookID"))
                {
                    WebHookID = Convert.ToUInt64(APPSettings.GetValues(ConfigKey)[0]);
                }
            }

            // Start a control thread for handling the discord??
            IRCMaster = new Thread(new ThreadStart(ComManager));
            IRCMaster.Start();

            // Start a control thread for handling the IRC side of things
            IRCConManager = new Thread(new ThreadStart(IRCCons));
            IRCConManager.Start();

            // Start a thread for handling data in and out to IRC Bots
            IRCConProcessor = new Thread(new ThreadStart(IRCProcessor));
            IRCConProcessor.Start();

            // Start the discord ASYNC shit fest.
            new Program().MainAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        ///  IRC Controller
        /// </summary>

        private static void IRCProcessor()
        {

        }

        /// <summary>
        ///  IRC Controller
        ///  This thread specifically deals with the connection process of IRC Bots and base functions like PING/PONG
        ///  and the initial nickname/username
        /// </summary>

        private static void IRCCons()
        {
            string IRCChannel = string.Empty;
            string IRCServer = string.Empty;
            string IRCPort = string.Empty;
            while (!Config.TryGetValue("IRCChannel", out IRCChannel)) { Thread.Sleep(1); }
            while (!Config.TryGetValue("IRCServer", out IRCServer)) { Thread.Sleep(1); }
            while (!Config.TryGetValue("IRCPort", out IRCPort)) { Thread.Sleep(1); }

            IRCConnections = new Dictionary<string, IRCConnection>();
            /*
                IOStream = Connection.GetStream();
                InputStream = new StreamReader(IOStream);
                InputStream.BaseStream.ReadTimeout = 5;
                OutputStream = new StreamWriter(IOStream);
            */
            while (true)
            {
                // Are all bots in the connected state?
                lock (IRCConnections)
                {
                    foreach (KeyValuePair<string, IRCConnection> IRCSet in IRCConnections)
                    {
                        string BotID = IRCSet.Key;
                        IRCConnection Bot = IRCSet.Value;

                        // Make sure the states are in sync
                        if (!Bot.Connection.Connected)
                        {
                            if (Bot.State.Connecting)
                            {
                                Console.WriteLine("Bot Reset");
                                Bot.Reset();
                            }
                            else
                            {
                                Console.WriteLine("Connecting a bot");
                                Bot.Reset();
                                Bot.State.Connecting = true;
                                //Bot.Connection.Connect(IRCServer, Convert.ToInt32(IRCPort));
                                Bot.Connection.Connect(IRCServer, Convert.ToInt32(IRCPort));
                                Bot.IOStream = Bot.Connection.GetStream();
                                Bot.InputStream = new StreamReader(Bot.IOStream);
                                Bot.InputStream.BaseStream.ReadTimeout = 2;
                                Bot.OutputStream = new StreamWriter(Bot.IOStream)
                                {
                                    AutoFlush = true
                                };
                            }
                            continue;
                        }

                        // And alot of state changes
                        if (Bot.State.Connected)
                        {
                            // Do nothing 
                            continue;
                        }
                        else if (Bot.State.Connecting)
                        {
                            if (Bot.State.AuthSend)
                            {
                                try
                                {
                                    Bot.ReadBuffer.Add(Bot.InputStream.ReadLine());
                                }
                                catch
                                {
                                    // We do not care what the problem was
                                }
                            }
                            else
                            {
                                Bot.OutputStream.WriteLine(@"USER {0} USER1 USER2 :DiscordBirdge (nugget)", Bot.Profile.IRCUsername);
                                Bot.OutputStream.WriteLine(@"NICK {0}", Bot.Profile.IRCNickname);
                                Bot.State.AuthSend = true;
                            }
                        }
                        else
                        {
                            Console.WriteLine(
                                "ELSE HIT connected({0}) connecting({1}) authsend({2})",
                                Bot.State.Connected,
                                Bot.State.Connecting,
                                Bot.State.AuthSend
                            );
                            continue;
                        }

                        // Deal with possible reads here
                        // Did we get what we wanted to signify we are connected?
                        foreach (string IRCEvent in Bot.ReadBuffer)
                        {
                            string[] IRCRead = IRCEvent.Split(' ');
                            if (IRCRead.Length >= 2)
                            {
                                if (IRCRead[0].Equals("PING"))
                                {
                                    Bot.OutputStream.WriteLine("PONG " + IRCRead[1]);
                                }
                                else if (IRCRead[1].Equals("001"))
                                {
                                    Bot.State.Connected = true;
                                    Bot.State.Connecting = false;
                                    Bot.OutputStream.WriteLine(@"JOIN {0}", IRCChannel);
                                }
                            }
                        }

                        // Clear the ReadBuffer
                        Bot.ReadBuffer.Clear();

                        // Need continue to skip this
                        Bot.State.LastChange = DateTime.UtcNow;
                    }
                }
                Thread.Sleep(1000);
            }
        }

        /// <summary>
        /// ComManager distributes and sorts messages into the right queue for discord or IRC
        /// </summary>

        private static void ComManager()
        {
            string IRCChannel = string.Empty;
            string IRCServer = string.Empty;
            string IRCPort = string.Empty;
            string IRCNickname = string.Empty;
            string IRCNicknamePass = string.Empty;
            while (!Config.TryGetValue("IRCChannel", out IRCChannel)) { Thread.Sleep(1); }
            while (!Config.TryGetValue("IRCServer", out IRCServer)) { Thread.Sleep(1); }
            while (!Config.TryGetValue("IRCPort", out IRCPort)) { Thread.Sleep(1); }
            while (!Config.TryGetValue("IRCNickname", out IRCNickname)) { Thread.Sleep(1); }
            while (!Config.TryGetValue("IRCNicknamePass", out IRCNicknamePass)) { Thread.Sleep(1); }

            IRCConnection Bot = new IRCConnection(IRCServer,Convert.ToInt32(IRCPort),"DickSword","DiscoBot","master");

            while (true)
            {
                if ( (!Bot.State.Connecting && !Bot.State.Connected) || !Bot.Connection.Connected )
                {
                    Console.WriteLine("Connecting Main");
                    Bot.Reset();
                    Bot.State.Connecting = true;
                    Bot.Connection.Connect(IRCServer, Convert.ToInt32(IRCPort));
                    Bot.IOStream = Bot.Connection.GetStream();
                    Bot.InputStream = new StreamReader(Bot.IOStream);
                    Bot.InputStream.BaseStream.ReadTimeout = 5;
                    Bot.OutputStream = new StreamWriter(Bot.IOStream)
                    {
                        AutoFlush = true
                    };
                }
                else if (Bot.State.Connecting && !Bot.State.AuthSend)
                {
                    Console.WriteLine("Sending user/nick");
                    Bot.OutputStream.WriteLine(@"USER {0} USER1 USER2 :DiscordBirdge (nugget)", "DiscoBot");
                    Bot.OutputStream.WriteLine(@"NICK {0}", IRCNickname);
                    Bot.State.AuthSend = true;
                }
                else
                {
                    // Read all messages first - this will always fail
                    try { Bot.ReadBuffer.Add(Bot.InputStream.ReadLine()); }
                    catch { }
                }

                // If we have nothing to read may as well goto the next iteration
                if (Bot.ReadBuffer.Count == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Deal with IRC cack
                foreach (string IRCEvent in Bot.ReadBuffer)
                {
                    if (IRCEvent == null) { continue; }
                    Console.WriteLine("Read: {0}", IRCEvent);
                    string[] IRCRead = IRCEvent.Split(' ');
                    if (IRCRead[0].Equals("PING"))
                    {
                        Bot.OutputStream.WriteLine("PONG " + IRCRead[1]);
                    }
                    else if (IRCRead.Length >= 2)
                    {
                        if (IRCRead[1].Equals("001"))
                        {
                            Bot.State.Connected = true;
                            Bot.State.Connecting = false;
                            Bot.OutputStream.WriteLine("PRIVMSG NickServ :IDENTIFY {0}", IRCNicknamePass);
                        }
                        else if (IRCRead.Length < 4) { continue; }
                        else if (
                            IRCRead[0].Equals(":NickServ!services@services.hybrid.local") && 
                            IRCRead[3].Equals(":Password") && 
                            IRCRead[4].Equals("accepted")
                        )
                        {
                            Bot.OutputStream.WriteLine("JOIN {0}", IRCChannel);
                        }
                    }
                    
                    //:NickServ!services@services.hybrid.local NOTICE DickSword :Password accepted - you are now recognized.
                }

                Bot.ReadBuffer.Clear();
            }
        }

        /// <summary>
        /// Discord related async functions
        /// </summary>

        public async Task MainAsync()
        {
            bool ErrorCondition = false;
            if (
                Config.TryGetValue("WebHookToken", out string WebHookToken)
            )
            {
                try
                {
                    _webhook = new DiscordWebhookClient(WebHookID, WebHookToken);
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
            while (!Shutdown)
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
                        new DiscordProfile
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
            // The bot should never respond to itself.
            if ( message.Author.Id == _discobot.CurrentUser.Id || message.Author.Id == WebHookID )
            {
                return;
            }

            BufferFromDiscord.Add(
                new DataPacket
                {
                    Identifier = message.Author.Id.ToString(),
                    Message = message.Content,
                    Discriminator = message.Author.Discriminator
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
                if (Registry.TryGetValue(Username, out DiscordProfile User))
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

    internal class IRCConnection
    {
        public TcpClient Connection { get; set; }
        public NetworkStream IOStream { get; set; }
        public StreamReader InputStream { get; set; }
        public StreamWriter OutputStream { get; set; }
        public IRCState State { get; set; }
        public List<String> ReadBuffer { get; set; }
        public IRCProfile Profile { get; set; }
        public string DiscordUnique { get; set; }
        public Queue<DataPacket> FromDiscord { get; set; }
        public IRCConnection(string IRCServer, int IRCPort, string IRCNickname, string IRCUsername, string DiscordRef )
        {
            FromDiscord = new Queue<DataPacket>();
            DiscordUnique = DiscordRef;
            Profile = new IRCProfile
            {
                IRCNickname = IRCNickname,
                IRCUsername = IRCUsername
            };
            Connection = new TcpClient();
            ReadBuffer = new List<String>();
            State = new IRCState();
        }
        internal void Reset()
        {
            State = new IRCState();
            Connection = new TcpClient();
        }
    }

    internal class IRCState
    {
        public bool Connecting { get; internal set; }
        public bool Connected { get; internal set; }
        public bool AuthSend { get; internal set; }
        public DateTime LastChange { get; internal set; }
        public bool NickAuth { get; internal set; }
        public bool Joined { get; internal set; }
    }

    internal class IRCProfile
    {
        public string IRCUsername { get; internal set; }
        public string IRCNickname { get; internal set; }
        public string IRCRealname { get; internal set; }
    }

    internal class DiscordProfile
    {
        public string IRCUnique { get; internal set; }
        public UInt64 DiscordUID { get; internal set; }
        public string DiscordUsername { get; internal set; }
        public string DiscordNickname { get; internal set; }
    }

    internal class DataPacket
    {
        public string Identifier { get; internal set; }
        public string Message { get; internal set; }
        public string Discriminator { get; internal set; }
    }
}