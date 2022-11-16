using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RplaceServer.Events;
using RplaceServer.Exceptions;
using WatsonWebsocket;
using ClientConnectedEventArgs = WatsonWebsocket.ClientConnectedEventArgs;

namespace RplaceServer;

public class SocketServer
{
    private readonly HttpClient httpClient = new();
    private readonly WatsonWsServer app;
    private readonly GameData gameData;
    private readonly string origin;
    
    public event EventHandler ChatMessageReceived;
    public event EventHandler PixelPlacementReceived;
    public event EventHandler PlayerConnected;
    public event EventHandler PlayerDisconnected;

    public SocketServer(GameData data, string certPath, string keyPath, string origin, bool ssl, int port, Logger<Action>? logger = null)
    {
        //TODO: Make my own watson fork, that has a mentally sane certificate implementation, and a proper unique way to identify clients.
        app = new WatsonWsServer("localhost", port, ssl);
        gameData = data;
        this.origin = origin;

        try
        {
            var boardFile = File.ReadAllBytes(Path.Join(gameData.CanvasFolder, "place"));
            if (boardFile.Length == 0) throw new NoCanvasFileFoundException("Could not locate canvas file at", Path.Join(gameData.CanvasFolder, "place"));
            gameData.Board = boardFile;
        }
        catch (Exception exception)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[WARNING]: " + exception.Message);
            Console.ResetColor();

            gameData.Board = new byte[gameData.BoardWidth * gameData.BoardHeight];

            if (!Directory.Exists(gameData.CanvasFolder))
            {
                Directory.CreateDirectory(gameData.CanvasFolder);
                
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[INFO]: Created new canvas folder.");
                Console.ResetColor();
            }
            
            File.WriteAllBytes(Path.Join(gameData.CanvasFolder, "place"), gameData.Board);
        }
        
        //Make a canvas save file just before the program exits.
        AppDomain.CurrentDomain.ProcessExit += (sender, e) => { File.WriteAllBytes(Path.Join(gameData.CanvasFolder, "place"), gameData.Board); };
    }

    public async Task Start()
    {
        app.ClientConnected += ClientConnected;
        app.MessageReceived += MessageReceived;
        app.ClientDisconnected += ClientDisconnected;

        await app.StartAsync();
    }

    private void ClientConnected(object? sender, ClientConnectedEventArgs args)
    {
        var idIpPort = GetIdIpPort(args.Client.IpPort);

        if (gameData.UseCloudflare &&
            args.HttpRequest.Headers.Get(Array.IndexOf(args.HttpRequest.Headers.AllKeys, "origin")) !=
            origin || gameData.Bans.Contains(args.Client.IpPort) || idIpPort.StartsWith("%"))
        {
            gameData.Bans.Add(idIpPort);
            app.DisconnectClient(args.Client);
            return;
        }
        
        //Send player cooldown + other data
        gameData.Clients.Add(args.Client, new SocketClient(idIpPort));
        var buffer = new byte[9];
        buffer[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan()[1..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan()[5..], gameData.Cooldown);
        app.SendAsync(args.Client, buffer);
        
        // Send player palette data (if using a custom palette)
        if (gameData.Palette is not null)
        {
            var palette = gameData.Palette.Select(Convert.ToUInt32).ToArray();
            var paletteBuffer = new byte[1 + palette.Length * 4];
            paletteBuffer[0] = 0;
            Buffer.BlockCopy(palette, 0, paletteBuffer, 1, palette.Length * 4);
            app.SendAsync(args.Client, paletteBuffer);
        }

        gameData.Players++;
    }
    
    private void MessageReceived(object? sender, MessageReceivedEventArgs args)
    {
        var idIpPort = GetIdIpPort(args.Client.IpPort);
        
        switch (args.Data[0])
        {
            case 15:
                if (gameData.Clients[args.Client].LastChat.AddMilliseconds(2500) > DateTimeOffset.Now || args.Data.Count > 400) return;
                gameData.Clients[args.Client].LastChat = DateTimeOffset.Now;
                
                ChatMessageReceived.Invoke(this, new ChatMessageEventArgs());

                foreach (var client in app.Clients)
                {
                    app.SendAsync(client, args.Data);
                }

                if (string.IsNullOrEmpty(gameData.WebhookUrl) || args.Data.Array is null) return;
                
                var rawMessage = Encoding.UTF8.GetString(args.Data.Array, 1, args.Data.Array.Length - 1).Replace("@", "");
                var text = rawMessage.Split("\n")[0];
                var name = new Regex("/\\W+/g").Replace(rawMessage.Split("\n")[1], "");
                var msgChannel = rawMessage.Split("\n")[2];

                var hook = $"{{'username': [{msgChannel}] {name}@rplace.tk`, 'content': {text}}}";
                httpClient.PostAsJsonAsync(gameData.WebhookUrl + "?wait=true", hook, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                break;
            case 16:
                var buffer = new byte[2];
                buffer[1] = 16;
                buffer[2] = 255;
                app.SendAsync(args.Client, buffer);
                break;
            case 99:
                break;
            case 20:
                break;
        }
        
        if (args.Data.Array?.Length < 6) return;
        var index = BinaryPrimitives.ReadUInt32BigEndian(args.Data.Array?[1..]);
        var colour = args.Data[5];

        if (index >= gameData.Board.Length || colour >= (gameData.Palette?.Count ?? 31)) return;
        var cd = gameData.Clients[args.Client].Cooldown;
        
        if (cd > DateTimeOffset.Now)
        {
            //reject
            var buffer = new byte[10];
            buffer[0] = 7;
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan()[1..], (int) cd.ToUnixTimeMilliseconds());
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan()[5..], (int) index);
            buffer[9] = gameData.Board[index];
            app.SendAsync(args.Client, buffer);
            return;
        }
        
        //accept
        gameData.Board[index] = colour;
        gameData.Clients[args.Client].Cooldown = DateTimeOffset.Now.AddSeconds(gameData.Cooldown - 500);
    }

    private void ClientDisconnected(object? sender, ClientDisconnectedEventArgs args)
    {
        gameData.Players--;
        gameData.Clients.Remove(args.Client);
    }

    /// <summary>
    /// Increases the size of a canvas/board, by a given width and height.
    /// </summary>
    /// <param name="widthIncrease">The increase in pixels on the X axis.</param>
    /// <param name="heightIncrease">The increase in pixels on the Y axis.</param>
    public void ExpandCanvas(int widthIncrease, int heightIncrease)
    {
        var newHeight = gameData.BoardHeight + heightIncrease;
        var newWidth = gameData.BoardWidth + widthIncrease;
        //Array.Copy(array, offset, result, 0, length);
        var newBoard = new byte[newHeight * newWidth];
        for (var y = 0; y < gameData.BoardHeight; y++)
        {
            newBoard[y * newWidth] = gameData.Board[y * gameData.BoardWidth];
        }

        gameData.Board = newBoard;
        gameData.BoardHeight = newHeight;
        gameData.BoardWidth = newWidth;
    }

    /// <summary>
    /// Sends a message inside of the game live chat to a specific client, or all connected clients.
    /// </summary>
    /// <param name="message">The message being sent.</param>
    /// <param name="channel">The channel that the message will be broadcast to.</param>
    /// <param name="client">The player that this chat message will be sent to, if no client provided, then it is sent to all</param>
    public void BroadcastChatMessage(string message, string channel, ClientMetadata? client = null)
    {
        var messageBytes = Encoding.UTF8.GetBytes($"\x0f{message}\nserver\n{channel}");
        messageBytes[0] = 15;

        if (client is null)
        {
            foreach (var c in app.Clients)
            {
                app.SendAsync(c, messageBytes);
            }
            
            return;
        }

        app.SendAsync(client, messageBytes);
    }

    /// <summary>
    /// Sets an area of the canvas to a specific colour.
    /// </summary>
    /// <param name="startX"></param>
    /// <param name="startY"></param>
    /// <param name="endX"></param>
    /// <param name="endY"></param>
    /// <param name="colour"></param>
    public void Fill(int startX, int startY, int endX, int endY, byte colour = 27)
    {
        while (startY < endY && startX < endX)
        {
            gameData.Board[startX++ + startY++ * gameData.BoardWidth] = colour;
        }
    }
    
    private string GetIdIpPort(string ipPort)
    {
        return ipPort;
        //return gameData.UseCloudflare
        //    ? args.HttpRequest.Headers.Get(Array.IndexOf(args.HttpRequest.Headers.AllKeys, "x-forwarded-for"))
        //    : args.IpPort;
    }
    
    
    
}