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

            string IRCChannel = string.Empty;
            while (!Config.TryGetValue("IRCChannel", out IRCChannel)) { Thread.Sleep(1); }

            while (true)
            {
                // Force clear the IRC Recv buffer
                ReadBuffer = new List<String>();

                // this block just greedily processes all message received from irc, it will always take at least 5ms.
                // we are using this as a cpu brake
                if (IRCConnection.Connected)
                {
                    // Read all messages first - this will always fail
                    try
                    {
                        while (true)
                        {
                            ReadBuffer.Add(I_Stream.ReadLine());
                        }
                    }
                    catch
                    {
                        // Do nothing
                    }
                }
                else
                {
                    // If we are not connected to IRC, then sleep 5ms anyway
                    Thread.Sleep(5);
                }

                // Alot of IF's
                foreach (string IRCEvent in ReadBuffer)
                {
                    string[] IRCRead = IRCEvent.Split(' ');
                    if (
                        !CState.NickAuth &&
                        IRCRead[0].Equals(":NickServ!services@services.hybrid.local") &&
                        IRCRead.Length > 8 
                    )
                    {
                        Console.WriteLine("Here: {0}",IRCRead[7]);
                        if (
                            IRCRead[6].Equals("registered") &&
                            Config.TryGetValue("IRCNicknamePass", out string IRCNicknamePass)
                        )
                        {
                            Console.WriteLine("Sending password: {0}", IRCNicknamePass);
                            _OStream.WriteLine("PRIVMSG NickServ :IDENTIFY {0}", IRCNicknamePass);
                        }
                        else if (
                            IRCRead[4].Equals("accepted") 
                        )
                        {
                            CState.NickAuth = true;
                            _OStream.WriteLine("JOIN {0}", IRCChannel);
                            _OStream.WriteLine("PRIVMSG CHANSERV :VOICE {0}", IRCChannel);
                        }
                    }
                    else if (IRCRead.Length >= 2)
                    {
                        if (IRCRead[0].Equals("PING"))
                        {
                            _OStream.WriteLine("PONG " + IRCRead[1]);
                        }
                        else if (IRCRead[1].Equals("001"))
                        {
                            CState.Connected = true;
                            CState.Connecting = false;
                        }
                        else if (IRCRead[1].Equals("PRIVMSG"))
                        {
                            string Nickname = String.Empty;
                            string Ident = String.Empty;
                            string Hostname = String.Empty;
                            string Message = String.Empty;

                            {
                                string Sign = IRCRead[0].Substring(1);
                                string[] NickSplit = Sign.Split('!');
                                Nickname = NickSplit[0];
                                string[] UserSplit = Sign.Split('@');
                                Ident = UserSplit[0];
                                Hostname = UserSplit[1];
                                for (int i = 3; i < IRCRead.Length; i++)
                                {
                                    Message = Message + IRCRead[i];
                                    if (i+1 < IRCRead.Length)
                                    {
                                        Message = Message + " ";
                                    }
                                }
                                Message = Message.Substring(1);
                            }

                            BufferToDiscord.Add(
                                new DataPacket
                                {
                                    Identifier = Nickname,
                                    Message = Message
                                }
                            );
                        }
                    }
                    
                    Console.WriteLine("Unhandled, {0}", IRCEvent);
                }

                // If we are connected and authed and happy send any messages we have waiting
                // In this situation we should really create a bot
                if (CState.Connected)
                {
                    // Primary work
                    if (BufferFromDiscord.Count > 0)
                    {
                        // Find the key information
                        DataPacket DataPkt = BufferFromDiscord.Take();

                        if (Registry.TryGetValue("discord:" + DataPkt.Identifier, out UserProfile OurUser)) {
                            _OStream.WriteLine(
                                "PRIVMSG {0} :<{1}> {2}",
                                IRCChannel,
                                OurUser.DiscordNickname ?? OurUser.DiscordUsername,
                                DataPkt.Message
                            );
                        }
                        else
                        {
                            _OStream.WriteLine(
                                "PRIVMSG {0} :<{1}> {2}",
                                IRCChannel,
                                "UnknownDiscordID",
                                DataPkt.Message
                            );
                        }
                        
                    }
                }
                else
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
                        I_Stream.BaseStream.ReadTimeout = 5;
                        _OStream = new StreamWriter(IOStream);
                        CState = new IRCState
                        {
                            Connecting = true
                        };
                    }
                    else if (
                        CState.Connecting && 
                        !CState.AuthSend &&
                        Config.TryGetValue("IRCNickname", out string IRCNickname)
                    )
                    {
                        // We have not sent authoritive info yet, lets do it!
                        Console.WriteLine("Sending auth details");
                        _OStream.WriteLine(@"USER DiscordB DiscordB DiscordB :DiscordBirdge (nugget)");
                        _OStream.WriteLine(@"NICK {0}",IRCNickname);
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

                // Send any pending data thats in the buffer
                if (IRCConnection.Connected)
                {
                    try
                    {
                        _OStream.Flush();
                    }
                    catch (Exception ee) {
                        Console.WriteLine("IRC Exception: {0}", ee.Message);
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
            // The bot should never respond to itself.
            if (
                message.Author.Id == _discobot.CurrentUser.Id
                ||
                message.Author.Id == WebHookID
            )
            {
                return;
            }

            BufferFromDiscord.Add(
                new DataPacket
                {
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
        public bool NickAuth { get; internal set; }
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
        public string Message { get; internal set; }
    }
}