using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using Bunkum.CustomHttpListener;
using Bunkum.CustomHttpListener.Listeners;
using Bunkum.CustomHttpListener.Listeners.Direct;
using Bunkum.CustomHttpListener.Parsing;
using Bunkum.CustomHttpListener.Request;
using Bunkum.HttpServer.Configuration;
using Bunkum.HttpServer.Database;
using Bunkum.HttpServer.Database.Dummy;
using Bunkum.HttpServer.Endpoints;
using Bunkum.HttpServer.Endpoints.Middlewares;
using Bunkum.HttpServer.Services;
using EasyHotReload;
using NotEnoughLogs;
using NotEnoughLogs.Sinks;

namespace Bunkum.HttpServer;

/// <summary>
/// The main class representing a Bunkum HTTP server and it's configuration.
/// </summary>
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public partial class BunkumHttpServer : IHotReloadable
{
    private readonly BunkumHttpListener _listener;
    public readonly Logger Logger;
    
    private IDatabaseProvider<IDatabaseContext> _databaseProvider = new DummyDatabaseProvider();
    private readonly List<Config> _configs;
    private readonly List<EndpointGroup> _endpoints = new();
    private readonly List<IMiddleware> _middlewares = new();
    private readonly List<Service> _services = new();

    private BunkumHttpServer(bool setListener, bool logToConsole, LoggerConfiguration? configuration)
    {
        configuration ??= LoggerConfiguration.Default;
        
        List<ILoggerSink> sinks = new();
        if(logToConsole) sinks.Add(new ConsoleSink());
        this.Logger = new Logger(sinks, configuration);
        
        this.Logger.LogInfo(BunkumCategory.Startup, $"Bunkum is storing its data at {BunkumFileSystem.DataDirectory}.");
        if (!BunkumFileSystem.UsingCustomDirectory)
        {
            this.Logger.LogInfo(BunkumCategory.Startup, "You can override where data is stored using the BUNKUM_DATA_FOLDER environment variable.");
        }

        BunkumConfig bunkumConfig = Config.LoadFromFile<BunkumConfig>("bunkum.json", this.Logger);
        this._configs = new List<Config>(2)
        {
            bunkumConfig,
        };

        if (setListener)
        {
            Uri listenEndpoint = new($"http://{bunkumConfig.ListenHost}:{bunkumConfig.ListenPort}");
            this._listener = new SocketHttpListener(listenEndpoint, bunkumConfig.UseForwardedIp, this.Logger);
        }
        else
        {
            this._listener = null!;
        }
        
        HotReloadRegistry.RegisterReloadable(this);
    }

    public BunkumHttpServer(LoggerConfiguration? configuration = null) : this(true, true, configuration) {}
    
    public BunkumHttpServer(BunkumHttpListener listener, bool logToConsole = true, LoggerConfiguration? configuration = null) : this(false, logToConsole, configuration)
    {
        this._listener = listener;
        if (listener is DirectHttpListener directListener)
        {
            directListener.Callback = context =>
            {
                this.CompleteRequestAsync(context).Wait();
            };
        }
    }

    /// <summary>
    /// Start the server in multithreaded mode. Caller is responsible for blocking.
    /// </summary>
    /// <param name="taskOverride">Override the number of tasks spun up.</param>
    public void Start(int? taskOverride = null)
    {
        this.RunStartupTasks();

        BunkumConfig? bunkumConfig = (BunkumConfig?)this._configs.FirstOrDefault(c => c is BunkumConfig);
        Debug.Assert(bunkumConfig != null);

        if (taskOverride == 0) return;

        int tasks = taskOverride ?? bunkumConfig.ThreadCount;

        this.Logger.LogInfo(BunkumCategory.Startup, $"Blocking in multithreaded mode with {tasks} tasks");

        for (int i = 0; i < tasks; i++)
        {
            int threadN = i + 1;
            Task.Factory.StartNew(async () =>
            {
                this.Logger.LogTrace(BunkumCategory.Startup, $"Spinning up task {threadN}/{tasks}");
                await this.Block();
            });
        }
    }
    
    /// <summary>
    /// Start the server in single-threaded mode. Bunkum is responsible for blocking.
    /// </summary>
    public async Task StartAndBlockAsync()
    {
        this.RunStartupTasks();
        this.Logger.LogInfo(BunkumCategory.Startup, "Blocking in single-threaded mode");
        await this.Block();
    }

    private void RunStartupTasks()
    {
        Stopwatch stopwatch = new();
        stopwatch.Start();
        
        this.Logger.LogInfo(BunkumCategory.Startup, "Starting up...");

        foreach (Service service in this._services)
        {
            this.Logger.LogInfo(BunkumCategory.Startup, $"Initializing service {service.GetType().Name}...");
            service.Initialize();
        }

        if (this._services.Count > 0)
        {
            string was = this._services.Count == 1 ? " was" : "s were";
            this.Logger.LogInfo(BunkumCategory.Startup, $"{this._services.Count} service{was} initialized.");
        }

        this.Logger.LogDebug(BunkumCategory.Startup, "Initializing database provider...");
        this._databaseProvider.Initialize();
        
        this.Logger.LogDebug(BunkumCategory.Startup, "Starting listener...");
        try
        {
            this._listener.StartListening();
        }
        catch(Exception e)
        {
            this.Logger.LogCritical(BunkumCategory.Startup, $"An exception occured while trying to start the listener: \n{e}");
            this.Logger.LogCritical(BunkumCategory.Startup, "Visit this page to view troubleshooting steps: " +
                                                             "https://littlebigrefresh.github.io/Docs/refresh-troubleshooting");
            
            this.Logger.Dispose();
            BunkumConsole.WaitForInputAndExit(1);
        }
        
        this.Logger.LogDebug(BunkumCategory.Startup, "Warming up database provider...");
        this._databaseProvider.Warmup();

        stopwatch.Stop();
        this.Logger.LogInfo(BunkumCategory.Startup, $"Ready to go! Startup tasks took {stopwatch.ElapsedMilliseconds}ms.");
    }
    
    private async Task Block()
    {
        while (!this._stopToken.IsCancellationRequested)
        {
            await this._listener.WaitForConnectionAsync(async context => await Task.Factory.StartNew(async () =>
            {
                await this.CompleteRequestAsync(context);
            }), this._stopToken.Token);
        }
        
        Debug.WriteLine("Block task was stopped");
    }

    internal async Task CompleteRequestAsync(ListenerContext context)
    {
        try
        {
            // Create a new lazy to get a database context, if the value is never accessed, a database instance is never passed
            Lazy<IDatabaseContext> database = new(this._databaseProvider.GetContext());

            // Handle the request
            await this.HandleRequest(context, database);

            if (database.IsValueCreated)
                database.Value.Dispose();
        }
        catch (Exception e)
        {
            this.Logger.LogError(BunkumCategory.Request, $"Failed to initialize request:\n{e}");
            context.ResponseCode = HttpStatusCode.InternalServerError;
            
            #if DEBUG
            context.Write(e.ToString());
            #else
            context.Write("Internal Server Error");
            #endif
        }
    }
    
    private readonly CancellationTokenSource _stopToken = new();

    /// <summary>
    /// Attempts to stop all block tasks, including those managed by the caller.
    /// If you are creating a server that does not span across the entire lifetime of the application, you are responsible for calling this to stop the server.
    /// </summary>
    public void Stop()
    {
        this._stopToken.Cancel();
        this.Logger.Dispose();
        HotReloadRegistry.UnregisterReloadable(this);
    }

    private async Task HandleRequest(ListenerContext context, Lazy<IDatabaseContext> database)
    {
        Stopwatch requestStopwatch = new();
        requestStopwatch.Start();

        try
        {
            // if (this.UseDigestSystem) this.VerifyDigestRequest(context, context.InputStream);
            Debug.Assert(context.InputStream.Position == 0); // should be at position 0 before we pass to the middleware chain
            
            // Setup a base middleware that calls Endpoints.
            // Passing in these parameters is a little janky in my opinion, but it gets the job done. 
            MainMiddleware mainMiddleware = new(this._endpoints,
                this.Logger,
                this._services,
                this._configs);

            Action next = () => { mainMiddleware.HandleRequest(context, database, null!); };
            
            foreach (IMiddleware middleware in this._middlewares)
            {
                // Without this copy, next won't be the same when we invoke next() in the middleware
                // By creating the copy, we ensure the pipeline stays in order.
                // https://www.jetbrains.com/help/rider/AccessToModifiedClosure.html
                Action nextCopy = next;
                
                // Every middleware triggers the last when next() is called.
                // For example:
                //   server.AddMiddleware<MiddlewareA>();
                //   server.AddMiddleware<MiddlewareB>();
                // results in:
                //   MiddlewareB -> MiddlewareA -> mainMiddleware
                // since adding MiddlewareB encapsulates the previous middleware, MiddlewareA.
                //
                // It's important to note that middlewares can "halt" this chain by simply not calling next().
                // This is by design.
                next = () => { middleware.HandleRequest(context, database, nextCopy); };
            }

            next(); // Trigger the pipeline
        }
        catch (Exception e)
        {
            this.Logger.LogError(BunkumCategory.Request, e.ToString());

            try
            {
                context.ResponseType = ContentType.Plaintext;
                context.ResponseCode = HttpStatusCode.InternalServerError;

#if DEBUG
                context.Write(e.ToString());
#else
                context.Write("Internal Server Error");
#endif
            }
            catch
            {
                if (Debugger.IsAttached) throw;
            }
        }
        finally
        {
            try
            {
                requestStopwatch.Stop();

                this.Logger.LogInfo(BunkumCategory.Request, $"Served request to {context.RemoteEndpoint}: " +
                                                          $"{(int)context.ResponseCode} {context.ResponseCode} on " +
                                                          $"{context.Method.ToString().ToUpper()} '{context.Uri.PathAndQuery}' " +
                                                          $"({requestStopwatch.ElapsedMilliseconds}ms)");

                await context.FlushResponseAndClose();
            }
            catch
            {
                // ignored
            }
        }
    }

    private Action<BunkumHttpServer>? _initialize;
    /// <summary>
    /// The initialization function for the server. Called after startup and after hot reload.
    /// You do not need to clear Bunkum's state during a hot reload - Bunkum will wipe everything for you. 
    /// </summary>
    /// <exception cref="InvalidOperationException">An initializer has already been declared, or the value is null.</exception>
    public Action<BunkumHttpServer>? Initialize
    {
        private get => this._initialize;
        set
        {
            if (this._initialize != null) throw new InvalidOperationException("Initializer has already been set.");
            if (value == null) throw new InvalidOperationException("Cannot set a null initializer");
            this._initialize = value;
            
            this._initialize.Invoke(this);
        }
    }

    void IHotReloadable.ProcessHotReload()
    {
        // If there's no initialization function, we can't do anything without destroying the server.
        if (this.Initialize == null)
        {
            this.Logger.LogWarning(BunkumCategory.Server, "The server's configuration does not properly support hot reloading.");
            this.Logger.LogWarning(BunkumCategory.Server, "To resolve this, move your initialization code into `BunkumHttpServer.Initialize`.");
            return;
        }
        
        this.Logger.LogDebug(BunkumCategory.Server, "Refreshing Bunkum's internal state for a hot reload, hold tight!");
        Stopwatch stopwatch = new();
        stopwatch.Start();

        // Back up the current BunkumConfig
        BunkumConfig? bunkumConfig = (BunkumConfig?)this._configs.FirstOrDefault(c => c is BunkumConfig);
        Debug.Assert(bunkumConfig != null);
        
        // Clear current internal state
        this._configs.Clear();
        this._configs.Add(bunkumConfig);
        
        this._endpoints.Clear();
        this._services.Clear();
        this._middlewares.Clear();
        this._databaseProvider.Dispose();
        
        // Refresh internal state using (potentially new) initialization function
        this.Initialize.Invoke(this);
        
        // Initialize the database provider
        this.Logger.LogDebug(BunkumCategory.Startup, "Initializing database provider...");
        this._databaseProvider.Initialize();
        this.Logger.LogDebug(BunkumCategory.Startup, "Warming up database provider...");
        this._databaseProvider.Warmup();
        
        this.Logger.LogInfo(BunkumCategory.Server, $"Successfully refreshed Bunkum's internal state in {stopwatch.ElapsedMilliseconds}ms.");
    }

    /// <summary>
    /// Manually add a group of endpoints.
    /// </summary>
    /// <param name="type">The type containing the endpoints to be used.</param>
    public void AddEndpointGroup(Type type)
    {
        EndpointGroup? doc = (EndpointGroup?)Activator.CreateInstance(type);
        Debug.Assert(doc != null);
        
        this._endpoints.Add(doc);
    }

    /// <summary>
    /// Manually add a group of endpoints.
    /// </summary>
    /// <typeparam name="TDoc">The type containing the endpoints to be used.</typeparam>
    public void AddEndpointGroup<TDoc>() where TDoc : EndpointGroup => this.AddEndpointGroup(typeof(TDoc));

    /// <summary>
    /// Discover all <see cref="EndpointGroup"/>s from an assembly and add them into Bunkum. 
    /// </summary>
    /// <param name="assembly">The assembly to scan from.</param>
    public void DiscoverEndpointsFromAssembly(Assembly assembly)
    {
        List<Type> types = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(EndpointGroup)))
            .ToList();

        foreach (Type type in types) this.AddEndpointGroup(type);
    }

    // ReSharper disable UnusedMember.Global
    // ReSharper disable MemberCanBePrivate.Global
    
    /// <summary>
    /// Define a database provider to be used. Once added, it can then be passed into Endpoints.
    /// </summary>
    /// <param name="provider">The provider to use.</param>
    public void UseDatabaseProvider(IDatabaseProvider<IDatabaseContext> provider)
    {
        this._databaseProvider = provider;
    }
    
    /// <summary>
    /// Adds a <see cref="Config"/> to Bunkum's internal list of configurations. These can then be passed into Endpoints or Services.
    /// </summary>
    /// <param name="config">An object extending <see cref="Config"/> that represents your server's configuration.</param>
    public void AddConfig(Config config)
    {
        this._configs.Add(config);
    }

    /// <summary>
    /// Adds a <see cref="Config"/> to Bunkum's internal list of configurations. These can then be passed into Endpoints or Services.
    /// </summary>
    /// <param name="config">An object extending <see cref="Config"/> that represents your server's configuration.</param>
    [Obsolete($"This method was renamed to {nameof(AddConfig)} for consistency. Please use the new method.")]
    public void UseConfig(Config config) => this.AddConfig(config);
    
    /// <summary>
    /// Load a <see cref="Config"/> from a .json file and add it into Bunkum's internal list of configurations, which can then be passed into Endpoints or Services.
    /// </summary>
    /// <param name="filename">What the config's filename should be stored as</param>
    /// <typeparam name="TConfig">An object extending <see cref="Config"/> that represents your server's configuration.</typeparam>
    /// <seealso cref="Config.LoadFromJsonFile"/>
    public void AddConfigFromJsonFile<TConfig>(string filename) where TConfig : Config, new()
    {
        TConfig config = Config.LoadFromJsonFile<TConfig>(filename, this.Logger);
        this.AddConfig(config);
    }

    /// <summary>
    /// Load a <see cref="Config"/> from a .json file and add it into Bunkum's internal list of configurations, which can then be passed into Endpoints or Services.
    /// </summary>
    /// <param name="filename">What the config's filename should be stored as</param>
    /// <typeparam name="TConfig">An object extending <see cref="Config"/> that represents your server's configuration.</typeparam>
    /// <seealso cref="Config.LoadFromFile"/>
    [Obsolete($"This method was renamed to {nameof(AddConfigFromJsonFile)} for consistency. Please use the new method.")]
    public void UseJsonConfig<TConfig>(string filename) where TConfig : Config, new()
        => this.AddConfigFromJsonFile<TConfig>(filename);
    
    public void AddMiddleware<TMiddleware>() where TMiddleware : IMiddleware, new() => this.AddMiddleware(new TMiddleware());
    public void AddMiddleware<TMiddleware>(TMiddleware middleware) where TMiddleware : IMiddleware => this._middlewares.Add(middleware);
}