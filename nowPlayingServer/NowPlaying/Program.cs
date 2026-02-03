using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Windows.Media.Control;
using System.Runtime.InteropServices.WindowsRuntime;

internal class Program
{
    // Media session manager and currently tracked session
    private static GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private static GlobalSystemMediaTransportControlsSession? _currentSession;

    // Single immutable state object (atomically replaced)
    private static MediaState _state = MediaState.Empty;

    // Playback simulation data (used only by polling loop)
    private static PlaybackSim _sim = PlaybackSim.Empty;

    // Used to prevent duplicate log spam
    private static string _lastLoggedTitle = "";
    private static string _lastLoggedArtist = "";
    private static string _lastLoggedPlaybackStatus = "";

    // Prevents overlapping media updates from multiple events
    private static readonly SemaphoreSlim _updateLock = new(1, 1);

    // JSON serialization options
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task Main()
    {
        using var cts = new CancellationTokenSource();

        // Graceful shutdown on Ctrl+C
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine($"{DateTime.Now} Ctrl+C detected. Shutting down...");
        };

        _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        Console.WriteLine($"{DateTime.Now} Listening for media changes...");

        // Initial session discovery
        await DiscoverAndSubscribeToSession(cts.Token);

        // Subscribe to session change events
        _sessionManager.CurrentSessionChanged += async (_, __) =>
            await SafeDiscoverAsync(cts.Token, "CurrentSessionChanged");


        _sessionManager.SessionsChanged += async (_, __) =>
            await SafeDiscoverAsync(cts.Token, "SessionsChanged");

        // Start background tasks
        var httpTask = StartHttpServerAsync(cts.Token);
        var pollTask = PollPlaybackPositionAsync(cts.Token);

        try
        {
            await Task.WhenAll(httpTask, pollTask);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        Console.WriteLine($"{DateTime.Now} Application stopped.");
    }

    // -------------------------
    // Session discovery & events
    // -------------------------

    private static async Task SafeDiscoverAsync(CancellationToken ct, string reason)
    {
        try
        {
            Console.WriteLine($"{DateTime.Now} {reason} event detected.");
            await DiscoverAndSubscribeToSession(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now} Error during {reason}: {ex}");
        }
    }

    private static async Task DiscoverAndSubscribeToSession(CancellationToken ct)
    {
        if (_sessionManager == null)
            return;

        GlobalSystemMediaTransportControlsSession? targetSession = null;

        var sessions = _sessionManager.GetSessions()?.ToList() ?? new();
        var current = _sessionManager.GetCurrentSession();

        if (sessions.Count > 0)
        {
            var playing = sessions
                .Where(s => s.GetPlaybackInfo()?.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                .ToList();

            if (playing.Count == 1)
            {
                targetSession = playing[0];
            }
            else if (playing.Count > 1)
            {
                targetSession = current ?? sessions.FirstOrDefault();
            }
            else
            {
                targetSession = current;
            }
        }

        if (!ReferenceEquals(targetSession, _currentSession))
            SubscribeToSession(targetSession);
        else if (_currentSession == null)
            SetState(MediaState.Empty);
    }

    private static void SubscribeToSession(GlobalSystemMediaTransportControlsSession? session)
    {
        // Unsubscribe from previous session
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _currentSession = session;

        if (_currentSession == null)
        {
            SetState(MediaState.Empty);
            _sim = PlaybackSim.Empty;
            LogState(force: true);
            return;
        }

        // Subscribe to new session events
        _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;

        _ = UpdateMediaInfoAsync(_currentSession, CancellationToken.None);
    }

    private static void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, object args)
        => _ = HandleSessionEventAsync(sender);

    private static void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender, object args)
        => _ = HandleSessionEventAsync(sender);

    private static async Task HandleSessionEventAsync(
        GlobalSystemMediaTransportControlsSession sender)
    {
        if (ReferenceEquals(sender, _currentSession))
            await UpdateMediaInfoAsync(sender, CancellationToken.None);
        else
            await DiscoverAndSubscribeToSession(CancellationToken.None);
    }

    // -------------------------
    // Media state update logic
    // -------------------------

    private static async Task UpdateMediaInfoAsync(
        GlobalSystemMediaTransportControlsSession session, CancellationToken ct)
    {
        await _updateLock.WaitAsync(ct);
        try
        {
            var media = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            // Update playback simulation info
            _sim = new PlaybackSim(
                timeline.Position,
                DateTime.UtcNow,
                playback?.PlaybackRate ?? 1.0,
                playback?.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline.EndTime
            );

            // Load thumbnail (if available)
            byte[]? image = null;
            if (media?.Thumbnail != null)
            {
                using var s = await media.Thumbnail.OpenReadAsync();
                using var ms = new MemoryStream();
                await s.AsStreamForRead().CopyToAsync(ms, ct);
                image = ms.ToArray();
            }

            // Atomically replace state
            SetState(new MediaState(
                media?.Title ?? "",
                media?.Artist ?? "",
                session.SourceAppUserModelId ?? "",
                playback?.PlaybackStatus.ToString() ?? "Unknown",
                FormatTime(timeline.Position),
                FormatTime(timeline.StartTime),
                FormatTime(timeline.EndTime),
                image
            ));

            LogState(force: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now} Media update failed: {ex.Message}");
            SetState(MediaState.Empty);
            _sim = PlaybackSim.Empty;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    // -------------------------
    // Smooth playback position polling
    // -------------------------

    private static async Task PollPlaybackPositionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sim = _sim;
                var st = GetState();

                if (sim.IsPlaying)
                {
                    var elapsed = DateTime.UtcNow - sim.LastKnownTimestampUtc;
                    var pos = sim.LastKnownPosition +
                              TimeSpan.FromSeconds(elapsed.TotalSeconds * sim.PlaybackRate);

                    if (pos > sim.EndTime)
                        pos = sim.EndTime;

                    SetState(st with { Position = FormatTime(pos) });
                }
            }
            catch { /* polling must never crash */ }

            await Task.Delay(250, ct);
        }
    }

    // -------------------------
    // HTTP server
    // -------------------------

    private static async Task StartHttpServerAsync(CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:58888/");
        listener.Start();

        ct.Register(() => listener.Close());

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                _ = HandleRequestAsync(ctx, ct);
            }
            catch { break; }
        }
    }

    private static async Task HandleRequestAsync(
        HttpListenerContext ctx, CancellationToken ct)
    {
        var res = ctx.Response;

        // Basic headers (CORS + no-cache)
        res.Headers.Add("Access-Control-Allow-Origin", "*");
        res.Headers.Add("Cache-Control", "no-store");

        var path = ctx.Request.Url?.AbsolutePath ?? "";

        if (path == "/media_info")
        {
            var buffer = JsonSerializer.SerializeToUtf8Bytes(GetState(), _jsonOptions);
            res.ContentType = "application/json";
            res.ContentLength64 = buffer.Length;
            await res.OutputStream.WriteAsync(buffer, ct);
        }
        else if (path == "/media_image")
        {
            var img = GetState().ImageBytes;
            if (img != null)
            {
                res.ContentType = "image/*";
                res.ContentLength64 = img.Length;
                await res.OutputStream.WriteAsync(img, ct);
            }
            else
            {
                res.StatusCode = 404;
            }
        }
        else
        {
            res.StatusCode = 404;
        }

        res.OutputStream.Close();
    }

    // -------------------------
    // Helpers
    // -------------------------

    private static void SetState(MediaState state)
        => _state = state;

    private static MediaState GetState()
        => _state;

    private static string FormatTime(TimeSpan t)
        => t == TimeSpan.Zero || t == TimeSpan.MaxValue
            ? ""
            : t.ToString(@"hh\:mm\:ss\.fff");

    private static void LogState(bool force)
    {
        var s = _state;
        if (force ||
            s.Title != _lastLoggedTitle ||
            s.Artist != _lastLoggedArtist ||
            s.Status != _lastLoggedPlaybackStatus)
        {
            Console.WriteLine($"{DateTime.Now} {s.Status}: {s.Title} - {s.Artist}");
            _lastLoggedTitle = s.Title;
            _lastLoggedArtist = s.Artist;
            _lastLoggedPlaybackStatus = s.Status;
        }
    }
}

// -------------------------
// Immutable data models
// -------------------------

internal record MediaState(
    string Title,
    string Artist,
    string App,
    string Status,
    string Position,
    string Start,
    string End,
    byte[]? ImageBytes)
{
    public static MediaState Empty =>
        new("(none)", "", "", "Stopped", "", "", "", null);
}

internal record PlaybackSim(
    TimeSpan LastKnownPosition,
    DateTime LastKnownTimestampUtc,
    double PlaybackRate,
    bool IsPlaying,
    TimeSpan EndTime)
{
    public static PlaybackSim Empty =>
        new(TimeSpan.Zero, DateTime.UtcNow, 1.0, false, TimeSpan.MaxValue);
}

