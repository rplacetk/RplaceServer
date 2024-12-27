// HTTPOfficial
// Copyright (C) 2024 Zekiah-A (https://github.com/Zekiah-A)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthWorkerShared;
using CensorCore;
using DataProto;
using HTTPOfficial.ApiModel;
using HTTPOfficial.DataModel;
using HTTPOfficial.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using WatsonWebsocket;

namespace HTTPOfficial;

/// <summary>
/// Central rplace global auth server, intended to act as a backbone for global accounts, instance creation and posts.
/// Test with:
/// ASPNETCORE_ENVIRONMENT=development; dotnet run
/// </summary>
internal static partial class Program
{
    private static Configuration globalConfig;
    private static WebApplication app;
    private static ILogger logger;
    private static HttpClient httpClient;
    private static JsonSerializerOptions defaultJsonOptions;
    private static AIService nudeNetAiService;
    private static CancellationTokenSource serverShutdownToken;

    [GeneratedRegex(@"^.{3,32}#[0-9]{4}$")]
    private static partial Regex TwitterHandleRegex();

    [GeneratedRegex(@"^(/ua/)?[A-Za-z0-9_-]+$")]
    private static partial Regex RedditHandleRegex();

    public static async Task Main(string[] args)
    {
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), "server_config.json");
        var instancesPath = Path.Combine(Directory.GetCurrentDirectory(), "Instances");
        var logsPath = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
        if (!Directory.Exists(logsPath))
        {
            Directory.CreateDirectory(logsPath);
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddFile(options =>
            {
                options.RootPath = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            });
        });
        logger = loggerFactory.CreateLogger("Program");

        void CreateNewConfig()
        {
            // Create config
            logger.LogWarning("Could not find server config file, at {configPath}", configPath);
            var defaultConfiguration = new Configuration
            {
                Version = Configuration.CurrentVersion,
                InstanceKey = RandomNumberGenerator.GetHexString(96),
                DefaultInstances =
                [
                    new Instance("server.rplace.live", true)
                    {
                        VanityName = "canvas1",
                        FileServerLocation = "raw.githubusercontent.com/rplacetk/canvas1/main",
                        Legacy = true
                    },
                    new Instance("server.rplace.live/testws", true)
                    {
                        VanityName = "placetest",
                        FileServerLocation = "raw.githubusercontent.com/rplacetk/canvas1/main",
                        Legacy = true
                    }
                ],
                ServerConfiguration = new ServerConfiguration
                {
                    Origin = "https://rplace.live",
                    Port = 8080,
                    SocketPort = 450,
                    UseHttps = false,
                    CertPath = "PATH_TO_CA_CERT",
                    KeyPath = "PATH_TO_CA_KEY",
                },
                PostsConfiguration = new PostsConfiguration
                {
                    PostsFolder = "Posts",
                    PostLimitSeconds = 60,
                    PostContentAllowedDomains = [ 
                        "rplace.tk", "rplace.live", "discord.gg", "twitter.com", "wikipedia.org",
                        "reddit.com", "discord.com", "x.com", "youtube.com", "t.me", "discord.com",
                        "tiktok.com", "twitch.tv", "fandom.com", "instagram.com", "canv.tk", "chit.cf",
                        "github.com", "openmc.pages.dev", "count.land"
                    ],
                    MinBannedContentPerceptualPercent = 80
                },
                AccountConfiguration = new AccountConfiguration
                {
                    AccountTierInstanceLimits = new Dictionary<AccountTier, int>
                    {
                        { AccountTier.Free, 2 },
                        { AccountTier.Bronze, 5 },
                        { AccountTier.Silver, 10 },
                        { AccountTier.Gold, 25 },
                        { AccountTier.Administrator, 50 }
                    }
                },
                EmailConfiguration = new EmailConfiguration
                {
                    SmtpHost = "SMTP_HOST",
                    SmtpPort = 587,
                    Username = "EMAIL_USERNAME",
                    Password = "EMAIL_PASSWORD",
                    FromEmail = "EMAIL_FROM",
                    FromName = "admin",
                    UseStartTls = true,
                    TimeoutSeconds = 30,
                    WebsiteUrl = "https://rplace.live",
                    SupportEmail = "admin@rplace.live"
                },
                AuthConfiguration = new AuthConfiguration
                {
                    JwtSecret = "JWT_SECRET",
                    JwtIssuer = "JWT_ISSUER",
                    JwtAudience = "WT_AUDIENCE",
                    JwtExpirationMinutes = 60,
                    RefreshTokenExpirationDays = 30,
                    VerificationCodeExpirationMinutes = 15,
                    MaxFailedVerificationAttempts = 5,
                    SignupRateLimitSeconds = 300,
                    FailedVerificationAttemptResetMinutes = 5
                },
            };
            var newConfigText = JsonSerializer.Serialize(defaultConfiguration, new JsonSerializerOptions()
            {
                WriteIndented = true,
                IndentSize = 1,
                IndentCharacter = '\t'
            });
            File.WriteAllText(configPath, newConfigText);
        }

        if (!File.Exists(configPath))
        {
            CreateNewConfig();
            logger.LogWarning("Config files recreated. Please check {currentDirectory} and run this program again.",
                Directory.GetCurrentDirectory());
            Environment.Exit(0);
        }
        if (!Directory.Exists(instancesPath))
        {
            Directory.CreateDirectory(instancesPath);
        }

        var configData = JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(configPath));
        if (configData is null || configData.Version < Configuration.CurrentVersion)
        {
            var oldConfigPath = configPath + ".old";
            logger.LogWarning("Current config file is invalid or outdated, moving to {oldConfigDirectory}. Config files recreated. Check {currentDirectory} and run this program again.",
                oldConfigPath, Directory.GetCurrentDirectory());
            File.Move(configPath, oldConfigPath);
            CreateNewConfig();
            Environment.Exit(0);
        }
        globalConfig = configData;

        // Main server
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.FullName,
            ContentRootPath = Path.GetFullPath(Directory.GetCurrentDirectory()),
            WebRootPath = "/",
            Args = args
        });

        // Register config
        builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);

        // Register Configuration class with default values and bind from configuration
        builder.Configuration.Bind(globalConfig);
        builder.Services.Configure<Configuration>(builder.Configuration);
        builder.Services.Configure<ServerConfiguration>(builder.Configuration.GetSection("ServerConfiguration"));
        builder.Services.Configure<PostsConfiguration>(builder.Configuration.GetSection("PostsConfiguration"));
        builder.Services.Configure<AccountConfiguration>(builder.Configuration.GetSection("AccountConfiguration"));
        builder.Services.Configure<AuthConfiguration>(builder.Configuration.GetSection("AuthConfiguration"));
        builder.Services.Configure<EmailConfiguration>(builder.Configuration.GetSection("EmailConfiguration"));

        // Swagger service
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSwaggerGen();
            builder.Services.AddEndpointsApiExplorer();
        }

        // Logger service
        builder.Services.AddSingleton<ILogger>(logger);

        // SMTP email sending service
        builder.Services.AddSingleton<EmailService>();

        builder.Services.AddDbContext<DatabaseContext>(options =>
        {
            options.UseSqlite("Data Source=server.db");
        });

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        builder.Services.AddCors(cors =>
        {
            cors.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin();
                policy.AllowAnyHeader();
                policy.AllowAnyMethod();
            });
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            var certPath = globalConfig.ServerConfiguration.CertPath;
            var keyPath = globalConfig.ServerConfiguration.KeyPath;
            if (string.IsNullOrEmpty(certPath) || string.IsNullOrEmpty(keyPath))
            {
                return;
            }

            var certificate = LoadCertificate(certPath, keyPath);
            options.ConfigureHttpsDefaults(httpsOptions =>
            {
                httpsOptions.ServerCertificate = certificate;
            });
        });

        builder.Configuration["Kestrel:Certificates:Default:Path"] = globalConfig.ServerConfiguration.CertPath;
        builder.Configuration["Kestrel:Certificates:Default:KeyPath"] = globalConfig.ServerConfiguration.KeyPath;

        app = builder.Build();
        app.Urls.Add($"{(globalConfig.ServerConfiguration.UseHttps ? "https" : "http")}://*:{globalConfig.ServerConfiguration.Port}");
        app.UseStaticFiles();

        app.UseCors(policy =>
        {
            policy.AllowAnyMethod()
                .AllowAnyHeader()
                .SetIsOriginAllowed(_ => true)
                .AllowCredentials();
        });

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });

        var wsServer = new WatsonWsServer(globalConfig.ServerConfiguration.SocketPort, globalConfig.ServerConfiguration.UseHttps, globalConfig.ServerConfiguration.CertPath, globalConfig.ServerConfiguration.KeyPath);

        // Vanity -> URL of actual socket server & board, done by worker clients on startup
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "web:Rplace.Live AuthServer v1.0 (by Zekiah-A)");

        // Used by worker servers + async communication
        var registeredVanities = new Dictionary<string, string>();
        var workerClients = new Dictionary<ClientMetadata, WorkerInfo>();
        var workerRequestId = 0;
        var workerRequestQueue = new ConcurrentDictionary<int, TaskCompletionSource<byte[]>>(); // ID, Data callback

        // Auth - Used when transitioning client from open to message handlers, periodic routines, etc
        var authorisedClients = new Dictionary<ClientMetadata, int>();

        // Used by reddit auth
        var refreshTokenAuthDates = new Dictionary<string, DateTime>();
        var refreshTokenAccessTokens = new Dictionary<string, string>();

        // Used by normal accounts
        var redditSerialiserOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        defaultJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // Sensitive content detection with NudeNet / CensorCore
        const string modelName = "detector_v2_default_checkpoint.onnx";
        var modelPath = Path.Combine("Resources", modelName);
        var modelBytes = await File.ReadAllBytesAsync(modelPath);
        var imgSharp = new ImageSharpHandler(2048, 2048); // Max image size
        var handler = new BodyAreaImageHandler(imgSharp, OptimizationMode.Normal);
        nudeNetAiService = AIService.Create(modelBytes, handler, false);

        // Default canvas instances
        await InsertDefaultInstancesAsync();

        // Shutdown and exceptions
        serverShutdownToken = new CancellationTokenSource();

        Console.CancelKeyPress += async (_, _) =>
        {
            await wsServer.StopAsync(serverShutdownToken.Token);
            await serverShutdownToken.CancelAsync();
            Environment.Exit(0);
        };

        AppDomain.CurrentDomain.UnhandledException += async (_, exceptionEventArgs) =>
        {
            logger.LogError("Unhandled server exception: {exception}", exceptionEventArgs.ExceptionObject);
            await wsServer.StopAsync(serverShutdownToken.Token);
            await serverShutdownToken.CancelAsync();
            Environment.Exit(1);
        };

        ConfigureAuthEndpoints();
        ConfigureAccountEndpoints();
        ConfigurePostEndpoints();
        ConfigureInstanceEndpoints();

        await Task.WhenAll(app.RunAsync(), wsServer.StartAsync(serverShutdownToken.Token));
        await Task.Delay(-1, serverShutdownToken.Token);
    }

    private static async Task InsertDefaultInstancesAsync()
    {
        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        if (database is null)
        {
            throw new Exception("Couldn't insert default instances, db was null");
        }

        foreach (var defaultInstance in globalConfig.DefaultInstances)
        {
            if (!await database.Instances.AnyAsync(instance => instance.VanityName == defaultInstance.VanityName))
            {
                database.Instances.Add(defaultInstance);
            }
        }

        await database.SaveChangesAsync();
    }


    public static X509Certificate2 LoadCertificate(string certPath, string keyPath)
    {
        var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return cert;
    }

    private static List<string> ReadTxtListFile(string path)
    {
        return File.ReadAllLines(path)
            .Where(entry => !string.IsNullOrWhiteSpace(entry) && entry.TrimStart().First() != '#').ToList();
    }
}
