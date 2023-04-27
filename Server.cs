using System.Net.WebSockets;
using System.Reflection.Emit;
using System.Text.Json;
using ComputerUtils.Logging;
using ComputerUtils.Webserver;

namespace PicturePokerMPServer;

public class Server
{
    public HttpServer server;

    public Config config
    {
        get
        {
            return Env.config;
        }
    }

    public class Lobby
    {
        public List<Player> players { get; set; } = new List<Player>();
        public string id { get; set; } = "";
        public bool isPrivate { get; set; } = false;
        public DateTime lastActivity { get; set; } = DateTime.Now;
        
        public Lobby() {}

        public Lobby(string id)
        {
            this.id = id;
        }

        /// <summary>
        /// Populates a player with the name from the websocket message
        /// </summary>
        /// <param name="msg">websocket message</param>
        /// <param name="request">websocket handler</param>
        public void PopulatePlayer(WebsocketMessageHeaders msg, SocketServerRequest request)
        {
            int playerIndex = GetPlayerIndex(request);
            if (playerIndex == -1) return;
            bool sendJoinMessage = players[playerIndex].name != msg.player;
            players[playerIndex].name = msg.player;
            players[playerIndex].registered = true;
            if (sendJoinMessage)
            {
                Broadcast(JsonSerializer.Serialize(new ChatMessage(msg.player + " joined the lobby")),null); // send join message in chat
            }
            Broadcast(JsonSerializer.Serialize(new LobbyUpdated(this)),null); // broadcast lobby update
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
                if (players[i].handler.handler == request.handler)
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
                players.Add(new Player { handler = request });
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

                if (sender == null || players[i].handler.handler != sender.handler)
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
                if (players[i].handler.handler.closed || players[i].handler.handler.socket.State == WebSocketState.Closed || players[i].handler.handler.socket.State == WebSocketState.Aborted)
                {
                    string name = players[i].name;
                    players.RemoveAt(i);
                    i--;
                    if(name != "")
                        Broadcast(JsonSerializer.Serialize(new ChatMessage(name + " left the lobby")),null); // send leave message in chat
                }
            }
        }
    }

    public class Player
    {
        public string name { get; set; } = "";
        public bool registered { get; set; } = false;
        public bool ready { get; set; } = false;
        public SocketServerRequest handler = null;
    }

    public class LobbyUpdated
    {
        public string type { get; set; } = "LobbyUpdated";
        public string player { get; set; } = "::Server::";
        public Lobby data { get; set; } = new Lobby();
        
        public LobbyUpdated() {}
        public LobbyUpdated(Lobby lobby) { data = lobby; }
    }
    public class ChatMessage
    {
        public string type { get; set; } = "ChatMessage";
        public string player { get; set; } = "::Server::";
        public string data { get; set; } = "";
        
        public ChatMessage() {}
        public ChatMessage(string msg) { data = msg; }
    }
    public class WebsocketMessageHeaders
    {
        public string type { get; set; } = "";
        public string player { get; set; } = "";
    }

    public class PlayerFound
    {
        public string lobbyToUse { get; set; } = "";
        public PlayerFound() {}

        public PlayerFound(string lobbyCode)
        {
            lobbyToUse = lobbyCode;
        }
    }
    
    public Dictionary<string, Lobby> lobbies = new Dictionary<string, Lobby>();
    public List<SocketServerRequest> searchingForPlayers = new List<SocketServerRequest>();
    
    

    public void Start()
    {
        string frontend = Directory.Exists("../../../frontend/") ? "../../../frontend/" : "frontend/";
        server = new HttpServer();
        server.AddWSRoute("/lobbies/", request =>
        {
            string gameId = request.pathDiff;
            if (gameId == "") return;
            if (!lobbies.ContainsKey(gameId)) lobbies.Add(gameId, new Lobby(gameId));
            lobbies[gameId].AddClient(request);
            WebsocketMessageHeaders msg;
            try
            {
                msg = JsonSerializer.Deserialize<WebsocketMessageHeaders>(request.bodyString);
                lobbies[gameId].PopulatePlayer(msg, request);
            } catch (Exception e)
            {
                Logger.Log("Couldn't parse json and thus won't do anything with it", LoggingType.Warning);
            }

            lobbies[gameId].Broadcast(request.bodyString, request);
        }, true);
        server.AddWSRoute("/searchingforplayers", request =>
        {
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
            if (searchingForPlayers.Count >= 2)
            {
                // If 2 or more players are searching for a game, create a lobby and send the lobby code to both players
                string lobbyCode = GetNonExistentLobbyCode();
                
                // Create lobby
                lobbies.Add(lobbyCode, new Lobby(lobbyCode));
                request.SendString(JsonSerializer.Serialize(new PlayerFound(lobbyCode)));
                // Remove own request from the list
                searchingForPlayers.RemoveAll(x => x.handler == request.handler);
                
                // Send the lobby code to the other player
                searchingForPlayers[0].SendString(JsonSerializer.Serialize(new PlayerFound(lobbyCode)));
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
            request.SendString(JsonSerializer.Serialize(new PlayerFound(lobbyCode)));
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
            if(lobbies[lobbyCodes[i]].players.Count <= 0 && (DateTime.Now - lobbies[lobbyCodes[i]].lastActivity).TotalMinutes > 5) // after 5 mins of inactivity without players
            {
                lobbies.Remove(lobbyCodes[i]);
                lobbyCodes.RemoveAt(i);
                i--;
            }
        }
    }
}