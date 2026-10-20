using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ErsatzTV.Nounours.Audience;

/// <summary>
///     Compte les spectateurs en direct : chaque requête de flux IPTV (m3u8, segments HLS, .ts…) « touche »
///     le couple chaîne + spectateur. Un spectateur est anonymisé (empreinte de l'adresse IP et du lecteur).
///     Il est considéré comme actif tant qu'une requête est en cours ou qu'il en a fait une dans les 40 dernières secondes.
/// </summary>
public sealed partial class AudienceTracker
{
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(40);

    private readonly ConcurrentDictionary<(string Channel, string Viewer), ViewerState> _viewers = new();

    public static Func<HttpContext, Func<Task>, Task> Middleware => async (context, next) =>
    {
        string channel = HttpMethods.IsGet(context.Request.Method) ? ChannelFromPath(context.Request.Path.Value) : null;
        AudienceTracker tracker = channel is null ? null : context.RequestServices.GetService<AudienceTracker>();
        if (tracker is null)
        {
            await next();
            return;
        }

        string viewer = ViewerKey(
            context.Connection.RemoteIpAddress?.ToString() ?? "?",
            context.Request.Headers.UserAgent.ToString());

        using (tracker.Begin(channel, viewer))
        {
            await next();
        }
    };

    public static string ChannelFromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        Match match = StreamPathRegex().Match(path);
        return match.Success ? Uri.UnescapeDataString(match.Groups["channel"].Value) : null;
    }

    public IDisposable Begin(string channel, string viewer)
    {
        ViewerState state = _viewers.GetOrAdd((channel, viewer), _ => new ViewerState());
        lock (state)
        {
            state.InFlight++;
            state.LastSeen = DateTimeOffset.Now;
        }

        return new Release(state);
    }

    /// <summary>Spectateurs actifs par numéro de chaîne (avec leur empreinte).</summary>
    public Dictionary<string, List<string>> Active()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var result = new Dictionary<string, List<string>>();
        foreach (((string channel, string viewer), ViewerState state) in _viewers)
        {
            bool active;
            lock (state)
            {
                active = state.InFlight > 0 || now - state.LastSeen < ActiveWindow;
            }

            if (active)
            {
                if (!result.TryGetValue(channel, out List<string> list))
                {
                    result[channel] = list = [];
                }

                list.Add(viewer);
            }
            else if (now - state.LastSeen > TimeSpan.FromHours(1))
            {
                _viewers.TryRemove((channel, viewer), out _);
            }
        }

        return result;
    }

    public int ActiveCount(string channel) => Active().TryGetValue(channel, out List<string> list) ? list.Count : 0;

    private static string ViewerKey(string ip, string userAgent)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ip}|{userAgent}"));
        return Convert.ToHexString(hash, 0, 6);
    }

    // /iptv/channel/2.ts · /iptv/channel/2.1.m3u8 · /iptv/session/2/hls.m3u8 · /iptv/session/2/live001.ts
    // /iptv/hdhr/channel/2.ts · /iptv/hls-direct/2.ts
    [GeneratedRegex(
        @"^/iptv/(?:(?:hdhr/)?channel/(?<channel>[^/]+?)\.(?:ts|m3u8)|session/(?<channel>[^/]+)/|hls-direct/(?<channel>[^/]+?)(?:\.(?:ts|mkv|mp4))?$)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex StreamPathRegex();

    private sealed class ViewerState
    {
        public int InFlight;
        public DateTimeOffset LastSeen;
    }

    private sealed class Release(ViewerState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            lock (state)
            {
                state.InFlight = Math.Max(0, state.InFlight - 1);
                state.LastSeen = DateTimeOffset.Now;
            }
        }
    }
}
