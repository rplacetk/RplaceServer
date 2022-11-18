//rplace.tk
//Copyright (C) 2022 Zekiah-A (https://github.com/Zekiah-A)

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Text.Json;
using Nephrite;
using RplaceServer;

namespace TKOfficial;

public static class Program
{
    private const string ProgramConfigPath = "server_config.json";
    private const string SocketConfigPath = "game_server_config.json";
    private const string WebConfigPath = "canvas_server_config.json";

    private static readonly JsonSerializerOptions JsonOptions = new() {WriteIndented = true};

    private static List<string> replPrevious = new();
    private static int replPreviousIndex;

    public static async Task Main(string[] args)
    {
        var missing = CheckFilesMissing(new[] {ProgramConfigPath, SocketConfigPath, WebConfigPath});
        if (missing.Count != 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("[Warning]: Could not find config files: ");
            while (missing.Count > 0)
            {
                var missingPath = missing.Pop();
                Console.Write(missingPath + ", ");

                dynamic config = missingPath switch
                {
                    ProgramConfigPath => new ProgramConfig(true, 443, 8080, "", "", "https://rplace.tk", false, "Backups"),
                    SocketConfigPath => new SocketServerConfig(1000, 1000,  10, 10, true, new List<string>(), new List<string>(), null, null),
                    WebConfigPath => new WebServerConfig(6000),
                    _ => throw new ArgumentOutOfRangeException()
                };

                await File.WriteAllTextAsync(missingPath, JsonSerializer.Serialize(config, JsonOptions));
            }
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[INFO]: Config files recreated. Please check {Directory.GetCurrentDirectory()} and run this program again.");
            Console.ResetColor();
            Environment.Exit(0);
        }

        var programConfig = JsonSerializer.Deserialize<ProgramConfig>(await File.ReadAllTextAsync(ProgramConfigPath)) ?? throw new NullReferenceException();
        var socketConfig = JsonSerializer.Deserialize<SocketServerConfig>(await File.ReadAllTextAsync(SocketConfigPath)) ?? throw new NullReferenceException();
        var webConfig = JsonSerializer.Deserialize<WebServerConfig>(await File.ReadAllTextAsync(WebConfigPath)) ?? throw new NullReferenceException();
        
        var data = new GameData
        (
            socketConfig.Cooldown,
            socketConfig.CaptchaEnabled,
            socketConfig.Vips,
            socketConfig.Bans,
            socketConfig.Width,
            socketConfig.Height,
            webConfig.BackupFrequency,
            programConfig.UseCloudflare,
            programConfig.CanvasFolder,
            socketConfig.WebhookUrl,
            socketConfig.PaletteOverride
        );

        var server = new ServerInstance(
            data,
            programConfig.CertPath,
            programConfig.KeyPath,
            programConfig.Origin,
            programConfig.SocketPort,
            programConfig.HttpPort,
            programConfig.Ssl
        );
        
        await server.Start();
        await StartNephriteRepl();
    }
    
    private static async Task StartNephriteRepl()
    {
        var runner = new NephriteRunner();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"You have entered the rPlace server Nephrite REPL. Enter a command to run it.\n");
        Console.ResetColor();

        var input = "";
        Console.Write(">> ");

        while (true)
        {
            var key = Console.ReadKey();
            
            switch (key.Key)
            {
                case ConsoleKey.Backspace:
                    Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                    if (input?.Length < 1) continue;
                    input = input?[..^1];
                    Console.Write("\b \b");
                    continue;
                case ConsoleKey.UpArrow:
                    input = replPrevious.ElementAtOrDefault(^replPreviousIndex);
                    replPreviousIndex++;
                    continue;
            }

            input += key.KeyChar.ToString();
            if (key.Key != ConsoleKey.Enter) continue;
            
            Console.WriteLine();
            Console.Write(">> ");

            if (!string.IsNullOrEmpty(input))
            {
                await runner.Execute(input);
            }

            replPrevious.Add(input);
            input = "";
        }
    }

    private static Stack<string> CheckFilesMissing(IEnumerable<string> files)
    {
        var targets = new Stack<string>();

        foreach (var file in files)
            if (!File.Exists(file))
                targets.Push(file);

        return targets;
    }
}