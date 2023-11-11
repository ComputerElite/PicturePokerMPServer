using System.Net.WebSockets;
using System.Reflection.Emit;
using System.Text.Json;
using ComputerUtils.Logging;
using ComputerUtils.Webserver;

namespace PicturePokerMPServer;

public class Server
{
    public HttpServer server;
    public static bool automaticallyAddBots = true;
    public static int botCount = 1;

    public Config config
    {
        get
        {
            return Env.config;
        }
    }

    public class UserProfile
    {
        public PlayerColor color { get; set; } = new PlayerColor();
        public string loginToken { get; set; } = "";
    }

    public class UserProfileHandler
    {
        public static string profileLocation
        {
            get
            {
                return Env.dataDir + "profiles.json";
            }
        }
        public static List<UserProfile> profiles = new List<UserProfile>();
        public static void LoadProfiles()
        {
            if(!File.Exists(profileLocation)) File.WriteAllText(profileLocation, JsonSerializer.Serialize(new List<UserProfile>()));
            profiles = JsonSerializer.Deserialize<List<UserProfile>>(File.ReadAllText(profileLocation));
        }

        public static UserProfile GetUserProfileByLoginToken(string token)
        {
            return profiles.FirstOrDefault(x => x.loginToken == token);
        }

        public static PlayerColor GetPlayerColor(string loginToken)
        {
            UserProfile p = GetUserProfileByLoginToken(loginToken);
            if (p == null) return new PlayerColor();
            return p.color;
        }
    }
    
    public enum CardType
    {
        Cloud = 0,
        Mushroom = 1,
        Fireflower = 2,
        Luigi = 3,
        Mario = 4,
        Star = 5
    }

    public class Lobby
    {
        public List<Player> players { get; set; } = new List<Player>();
        public string id { get; set; } = "";
        public string host { get; set; } = "";
        public int betMultiplier { get; set; } = 1;
        public bool isPrivate { get; set; } = false;
        public bool inProgress { get; set; } = false;
        public int currentRound { get; set; } = 0;
        public int roundCount { get; set; } = 5;
        public int playersExpected { get; set; } = 0;
        public DateTime lastActivity { get; set; } = DateTime.Now;
        
        public Dictionary<string, PlayerNumber> PlayerNumbers = new Dictionary<string, PlayerNumber>();

        public Lobby() {}

        public Lobby(string id)
        {
            this.id = id;
        }

        public void UpdateBots(string msgType)
        {
            UpdateBots(x =>
            {
                if (msgType == "MyCards")
                {
                    // Human player sent cards, generate random cards and send them too
                    List<CardType> cards = new List<CardType>();
                    for (int i = 0; i < 5; i++)
                    {
                        cards.Add((CardType)Random.Shared.Next(0, 6));
                    }
                    Broadcast(JsonSerializer.Serialize(new WebsocketMessage<List<CardType>> {data = cards, type = "MyCards", id = x.id }), null);
                }else if (msgType == "DrawHoldPressed")
                {
                    // We also press the button when the player presses it
                    Broadcast(JsonSerializer.Serialize(new WebsocketMessage<string> {data = "", type = "DrawHoldPressed", id = x.id }), null);
                } else if (msgType == "ReadyForNextRound")
                {
                    x.readyForNextRound = true;
                    Broadcast(JsonSerializer.Serialize(new WebsocketMessage<string> {data = "", type = "ReadyForNextRound", id = x.id }), null);
                }
            });
        }

        /// <summary>
        /// Populates a player with the name from the websocket message and updates the lobby accordingly
        /// </summary>
        /// <param name="msg">websocket message</param>
        /// <param name="request">websocket handler</param>
        public void PopulatePlayer(WebsocketMessageHeaders msg, SocketServerRequest request, string orgMsg)
        {
            int playerIndex = GetPlayerIndex(request);
            if (playerIndex == -1) return;
            bool sendJoinMessage = players[playerIndex].id != msg.id;
            players[playerIndex].name = msg.name;
            players[playerIndex].id = msg.id;
            if (host == "") host = msg.id;
            players[playerIndex].registered = true;
            if (msg.type == "Hello")
            {
                WebsocketMessage<string> login = JsonSerializer.Deserialize<WebsocketMessage<string>>(orgMsg);
                players[playerIndex].loginToken = login.login;
            }
            UpdatePlayerNumbers();
            players[playerIndex].color = UserProfileHandler.GetPlayerColor(players[playerIndex].loginToken);
            if (msg.type == "GameReady")
            {
                UpdateBots(x => { x.inGame = true; });
                players[playerIndex].inGame = true;
            }

            if (msg.type == "ReadyForNextRound")
            {
                players[playerIndex].readyForNextRound = true;
            }
            if (players.Where(x => x.registered).All(x => x.readyForNextRound) && (playersExpected == -1 || players.Count == playersExpected))
            {
                players.ForEach(x => x.readyForNextRound = false);
                // as next round can start, increment round counter
                currentRound++;
            }
            if (msg.type == "LobbyBetChange")
            {
                if (host == msg.id)
                {
                    // host changed bet
                    WebsocketMessage<int> bet = JsonSerializer.Deserialize<WebsocketMessage<int>>(orgMsg);
                    betMultiplier = bet.data;
                }
            }
            if (msg.type == "LobbyRoundChange")
            {
                if (host == msg.id)
                {
                    // host changed rounds
                    WebsocketMessage<int> rounds = JsonSerializer.Deserialize<WebsocketMessage<int>>(orgMsg);
                    roundCount = rounds.data;
                }
            }
            
            if (inProgress && playersExpected == players.Count)
            {
                // all players have joined
                playersExpected = -1;
            }

            if (msg.type == "Start")
            {
                inProgress = true;
                playersExpected = players.Count;
            }

            if (msg.type == "MyCoins")
            {
                WebsocketMessage<int> coins = JsonSerializer.Deserialize<WebsocketMessage<int>>(orgMsg);
                players[playerIndex].coins = coins.data;
            }

            if (msg.type == "Disconnect")
            {
                request.Close();
                CleanLobby();
                return;
            }
            if (sendJoinMessage)
            {
                Broadcast(JsonSerializer.Serialize(new ChatMessage(msg.name + " joined the lobby")),null); // send join message in chat
            }

            UpdateBots(msg.type);
            Broadcast(JsonSerializer.Serialize(new LobbyUpdated(this)),null); // broadcast lobby update
        }

        private void UpdateBots(Action<Player> action)
        {
            players.ForEach(x =>
            {
                if(x.isServerBot) action.Invoke(x);
            });
        }

        /// <summary>
        /// Makes sure players in the lobby are ascending, sets all player instances with their respective player number
        /// </summary>
        private void UpdatePlayerNumbers()
        {
            // Remove all players which aren't in the lobby anymore from the player numbers dictionary
            PlayerNumbers = PlayerNumbers.Where(x => players.Any(y => y.id == x.Key && y.registered)).ToDictionary(x => x.Key, x => x.Value);
            // Now make sure the playerNumbers have ascending values
            for (int i = 0; i < 4; i++)
            {
                if (!PlayerNumbers.ContainsValue((PlayerNumber)i))
                {
                    foreach (string playerId in PlayerNumbers.Keys)
                    {
                        // if the player number is greater than i, decrease it by one. This will make sure that the player numbers are ascending
                        if ((int)PlayerNumbers[playerId] > i) PlayerNumbers[playerId] = (PlayerNumber)((int)PlayerNumbers[playerId] - 1);
                    }
                }
            }
            // All all players which are registered but not in the player numbers dictionary yet
            foreach (Player player in players.Where(x => x.registered && !PlayerNumbers.ContainsKey(x.id)))
            {
                PlayerNumbers.Add(player.id, (PlayerNumber)PlayerNumbers.Count);
            }
            
            // Finally update all players with their respecting player number
            foreach (Player player in players)
            {
                if (!PlayerNumbers.ContainsKey(player.id)) return;
                player.playerNumber = PlayerNumbers[player.id];
            }
        }

        /// <summary>
        /// Gets the index in the players list of a client based on their websocket handler
        /// </summary>
        /// <param name="request">websocket handler</param>
        /// <returns>index of the player, -1 if not present</returns>
        public int GetPlayerIndex(SocketServerRequest request)
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].handler != null && players[i].handler.handler == request.handler)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Adds a client to the lobby if it's not present yet
        /// </summary>
        /// <param name="request"></param>
        public void AddClient(SocketServerRequest request)
        {
            lastActivity = DateTime.Now;
            if (GetPlayerIndex(request) == -1)
            {
                if (inProgress && playersExpected == -1)
                {
                    // Player is not allowed to join
                    request.Close();
                    return;
                }
                players.Add(new Player { handler = request });
                if (automaticallyAddBots)
                {
                    for (int i = 1; i <= botCount; i++)
                    {
                        players.Add(new Player()
                        {
                            id = "Bot" + i,
                            name = "Bot " + i,
                            loginToken = i.ToString(),
                            isServerBot = true,
                            registered = true,
                            playerNumber = (PlayerNumber)i,
                            coins = 30
                        });
                    }
                }
                request.SendString(JsonSerializer.Serialize(new LobbyUpdated(this))); // Send player who joined the status of the lobby
            }
        }

        /// <summary>
        /// Broadcast messages to all other players and remove disconnected players
        /// </summary>
        /// <param name="msg">msg to broadcast</param>
        /// <param name="sender">sender of the msg</param>
        public void Broadcast(string msg, SocketServerRequest sender)
        {
            CleanLobby();
            for (int i = 0; i < players.Count; i++)
            {

                if (!players[i].isServerBot && (sender == null || players[i].handler.handler != sender.handler))
                {
                    Logger.Log("Forwarding msg to " + players[i].handler.context.Request.RemoteEndPoint.Address);
                    players[i].handler.SendString(msg);
                }
            }
        }

        public void CleanLobby()
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].handler != null && (players[i].handler.handler.closed || players[i].handler.handler.socket.State != WebSocketState.Open))
                {
                    string name = players[i].name;
                    players.RemoveAt(i);
                    i--;
                    if (name != "")
                    {
                        // Broadcast lobby status and send player left lobby message
                        Broadcast(JsonSerializer.Serialize(new ChatMessage(name + " left the lobby")),null); // send leave message in chat
                        Broadcast(JsonSerializer.Serialize(new LobbyUpdated(this)),null); // broadcast lobby update
                    }
                }
            }
            // After lobby cleanup update player numbers
            UpdatePlayerNumbers();
        }

        public int GetPlayerCount()
        {
            return players.Count(x => x.registered);
        }
    }

    public class Player
    {
        public string name { get; set; } = "";
        public string id { get; set; } = "";

        public PlayerNumber playerNumber { get; set; } = PlayerNumber.P1;
        // login will not be sent. It'll get received and stored in the server with the Hello event
        public string loginToken = "";
        public bool registered { get; set; } = false;
        public PlayerColor color { get; set; } = new PlayerColor();
        public bool inGame { get; set; } = false;
        public bool ready { get; set; } = false;
        public bool isServerBot { get; set; } = false;
        public int coins { get; set; } = 0;
        public SocketServerRequest handler = null;
        public bool readyForNextRound = false;
    }

    public enum PlayerNumber
    {
        P1 = 0,
        P2 = 1,
        P3 = 2,
        P4 = 3
    }

    public class PlayerColor
    {
        public float r { get; set; } = 1f;
        public float g { get; set; } = 1f;
        public float b { get; set; } = 1f;
    }

    public class LobbyUpdated
    {
        public string type { get; set; } = "LobbyUpdated";
        public string player { get; set; } = "System";
        public Lobby data { get; set; } = new Lobby();
        
        public LobbyUpdated() {}
        public LobbyUpdated(Lobby lobby) { data = lobby; }
    }
    public class ChatMessage
    {
        public string type { get; set; } = "ChatMessage";
        public string player { get; set; } = "System";
        public string data { get; set; } = "";
        
        public ChatMessage() {}
        public ChatMessage(string msg) { data = msg; }
    }
    public class WebsocketMessageHeaders
    {
        public string type { get; set; } = "";
        public string id { get; set; } = "";
        public string name { get; set; } = "";
    }
    
    public class WebsocketMessage<T>
    {
        public string type { get; set; } = "";
        public string player { get; set; } = "";
        public string id { get; set; } = "";
        public string login { get; set; } = "";

        public T data { get; set; } = default(T);
    }

    public class PlayerFound
    {
        public string lobbyToUse { get; set; } = "";
        public int opponentsInLobby { get; set; } = 0;
        public PlayerFound() {}

        public PlayerFound(string lobbyCode, int opponentsInLobby)
        {
            lobbyToUse = lobbyCode;
            this.opponentsInLobby = opponentsInLobby;
        }
    }
    
    public Dictionary<string, Lobby> lobbies = new Dictionary<string, Lobby>();
    public List<SocketServerRequest> searchingForPlayers = new List<SocketServerRequest>();

    public static Version minGameVersion = new Version("1.3.0.0");

    public bool HandlGameVersion(SocketServerRequest request)
    {
        return false;
        string gameVersion = request.queryString.Get("version");
        if (gameVersion != null)
        {
            gameVersion = new string(gameVersion.Where(c => "0123456789.".Contains(c)).ToArray()); // remove everything which ain't a number or a dot
            Logger.Log("Game version is " + new Version(gameVersion) + " and min version is " + minGameVersion);
            if (new Version(gameVersion).CompareTo(minGameVersion) < 0)
            {
                request.SendString(JsonSerializer.Serialize(new WebsocketMessage<string> { type = "Error", data = "Your game version is outdated. Please update to the latest version." }));
                request.Close();
                return true;
            }
        }

        return false;
    }

    public void Start()
    {
        UserProfileHandler.LoadProfiles();
        string frontend = Directory.Exists("../../../frontend/") ? "../../../frontend/" : "frontend/";
        server = new HttpServer();
        server.AddWSRoute("/lobbies/", request =>
        {
            if (HandlGameVersion(request)) return;
            string gameId = request.pathDiff;
            if (gameId == "") return;
            if (!lobbies.ContainsKey(gameId)) lobbies.Add(gameId, new Lobby(gameId));
            lobbies[gameId].AddClient(request);
            WebsocketMessageHeaders msg;
            try
            {
                msg = JsonSerializer.Deserialize<WebsocketMessageHeaders>(request.bodyString);
                lobbies[gameId].PopulatePlayer(msg, request, request.bodyString);
            } catch (Exception e)
            {
                Logger.Log("Couldn't parse json and thus won't do anything with it", LoggingType.Warning);
            }

            lobbies[gameId].Broadcast(request.bodyString, request);
        }, true);
        server.AddWSRoute("/searchingforplayers", request =>
        {
            if (HandlGameVersion(request)) return;
            // Clean all closed connections
            for (int i = 0; i < searchingForPlayers.Count; i++)
            {
                if (searchingForPlayers[i].handler.closed || searchingForPlayers[i].handler.socket.State == WebSocketState.Closed || searchingForPlayers[i].handler.socket.State == WebSocketState.Aborted)
                {
                    searchingForPlayers.RemoveAt(i);
                    i--;
                }
            }
            
            bool containsClient = false;
            for (int i = 0; i < searchingForPlayers.Count; i++)
            {
                if (searchingForPlayers[i].handler == request.handler)
                {
                    containsClient = true;
                    break;
                }
            }
            if(!containsClient) searchingForPlayers.Add(request);
            // Check if a public lobby with less than 4 players exists
            Lobby l = lobbies.Values.FirstOrDefault(x => x.isPrivate == false && x.GetPlayerCount() < 4 && x.GetPlayerCount() > 1 && x.inProgress == false);
            if (l != null)
            {
                // A lobby with less than 4 players exists, join it
                request.SendString(JsonSerializer.Serialize(new PlayerFound(l.id, l.GetPlayerCount())));
                // Remove own request from the list
                searchingForPlayers.RemoveAll(x => x.handler == request.handler);
                return;
            }
            
            // If no lobby has been found wait for 2 players searching for opponents
            if (searchingForPlayers.Count >= 2)
            {
                // If 2 or more players are searching for a game, create a lobby and send the lobby code to both players
                string lobbyCode = GetNonExistentLobbyCode();
                
                // Create lobby
                lobbies.Add(lobbyCode, new Lobby(lobbyCode));
                request.SendString(JsonSerializer.Serialize(new PlayerFound(lobbyCode, 1)));
                // Remove own request from the list
                searchingForPlayers.RemoveAll(x => x.handler == request.handler);
                
                // Send the lobby code to the other player
                searchingForPlayers[0].SendString(JsonSerializer.Serialize(new PlayerFound(lobbyCode, 1)));
                searchingForPlayers.RemoveAt(0);
            }
        });
        server.AddRoute("GET", "/api/lobbies", request =>
        {
            CleanUpLobbies();
            request.SendString(JsonSerializer.Serialize(lobbies.Values), "application/json");
            return true;
        });
        server.AddRoute("GET", "/api/lobby/", request =>
        {
            CleanUpLobbies();
            if(!lobbies.ContainsKey(request.pathDiff)) request.SendString("{}", "application/json", 404);
            request.SendString(JsonSerializer.Serialize(lobbies[request.pathDiff]), "application/json");
            return true;
        });
        server.AddRoute("GET", "/api/createlobby/", request =>
        {
            CleanUpLobbies();
            string lobbyCode = GetNonExistentLobbyCode();
            // Create lobby
            lobbies.Add(lobbyCode, new Lobby(lobbyCode));
            lobbies[lobbyCode].isPrivate = true;
            request.SendString(JsonSerializer.Serialize(new PlayerFound(lobbyCode, 0)));
            return true;
        });
        server.AddRouteFile("/", frontend + "index.html", null);
        server.AddRouteFile("/style.css", frontend + "style.css", null);
        server.AddRouteFile("/script.js", frontend + "script.js", null);
        server.AddRoute("GET", "/game/", request =>
        {
            string folderPath = frontend + "game" + Path.DirectorySeparatorChar;
            string file = folderPath + request.pathDiff.Replace('/', Path.DirectorySeparatorChar);
            Logger.Log(folderPath);
            Dictionary<string, string> headers = new Dictionary<string, string>();
            if(file.EndsWith(".br")) headers.Add("Content-Encoding", "br");
            if (File.Exists(file)) request.SendFile(file, file.EndsWith(".wasm.br") ? "application/wasm" : "", 200, true, headers);
            else request.Send404();
            return true;
        }, true);
        server.StartServer(config.port);
    }

    string GetNonExistentLobbyCode()
    {
        string lobbyCode;
        while (lobbies.ContainsKey(lobbyCode = GetRandomLobbyCode()))
        {
                    
        }

        return lobbyCode;
    }

    string GetRandomLobbyCode()
    {
        return Random.Shared.Next(0x1000, 0xFFFF).ToString("X"); // generate 4 random hex digits
    }

    private void CleanUpLobbies()
    {
        List<string> lobbyCodes = lobbies.Keys.ToList();
        for (int i = 0; i < lobbyCodes.Count; i++)
        {
            lobbies[lobbyCodes[i]].CleanLobby();
            if(lobbies[lobbyCodes[i]].players.Count <= 0 && (DateTime.Now - lobbies[lobbyCodes[i]].lastActivity).TotalSeconds > 30) // after 30 sec of inactivity without players
            {
                lobbies.Remove(lobbyCodes[i]);
                lobbyCodes.RemoveAt(i);
                i--;
            }
        }
    }
}