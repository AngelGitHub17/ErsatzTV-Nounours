using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ErsatzTV.Application.Nounours;

/// <summary>
///     Télécommande d'OBS (obs-websocket v5, intégré à OBS 28 et plus : Outils › Paramètres du serveur WebSocket).
///     Sert à changer de scène (plateau, invité, caméra 2, écran…) depuis ErsatzTV : le multi-caméras se fait dans OBS,
///     l'antenne ne coupe pas.
/// </summary>
public static class ObsRemote
{
    public sealed record ObsScenes(string Current, List<string> Scenes);

    public static Task<ObsScenes> GetScenes(ObsSettings obs, CancellationToken cancellationToken) =>
        Session(obs, async socket =>
        {
            JsonNode data = await Request(socket, "GetSceneList", null, cancellationToken);
            var scenes = (data?["scenes"]?.AsArray() ?? [])
                .Select(s => s?["sceneName"]?.GetValue<string>())
                .Where(s => !string.IsNullOrEmpty(s))
                .Reverse() // OBS les donne de bas en haut
                .ToList();
            return new ObsScenes(data?["currentProgramSceneName"]?.GetValue<string>() ?? string.Empty, scenes);
        }, cancellationToken);

    public static Task<bool> SetScene(ObsSettings obs, string scene, CancellationToken cancellationToken) =>
        Session(obs, async socket =>
        {
            await Request(socket, "SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene }, cancellationToken);
            return true;
        }, cancellationToken);

    private static async Task<T> Session<T>(ObsSettings obs, Func<ClientWebSocket, Task<T>> work, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var socket = new ClientWebSocket();
        string host = string.IsNullOrWhiteSpace(obs.Host) || obs.Host == "0.0.0.0" ? "127.0.0.1" : obs.Host.Trim();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{host}:{obs.WebSocketPort}"), timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"OBS ne répond pas sur ws://{host}:{obs.WebSocketPort} : dans OBS, Outils › Paramètres du serveur WebSocket › « Activer le serveur WebSocket ».", ex);
        }

        // Hello (op 0) → Identify (op 1) → Identified (op 2)
        JsonNode hello = await Receive(socket, timeout.Token);
        var identify = new JsonObject { ["rpcVersion"] = 1 };
        JsonNode auth = hello?["d"]?["authentication"];
        if (auth is not null)
        {
            if (string.IsNullOrEmpty(obs.WebSocketPassword))
            {
                throw new InvalidOperationException("OBS demande un mot de passe WebSocket : recopiez-le dans les réglages OBS de la page Info.");
            }

            identify["authentication"] = Authentication(obs.WebSocketPassword, auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
        }

        await Send(socket, new JsonObject { ["op"] = 1, ["d"] = identify }, timeout.Token);
        JsonNode identified = await Receive(socket, timeout.Token);
        if (identified?["op"]?.GetValue<int>() != 2)
        {
            throw new InvalidOperationException("OBS a refusé la connexion (mot de passe WebSocket incorrect ?).");
        }

        T result = await work(socket);
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // déjà fermé
        }

        return result;
    }

    /// <summary>base64(sha256(base64(sha256(mot de passe + sel)) + défi)), comme le décrit obs-websocket.</summary>
    public static string Authentication(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private static async Task<JsonNode> Request(ClientWebSocket socket, string type, JsonObject data, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data is not null)
        {
            d["requestData"] = data;
        }

        await Send(socket, new JsonObject { ["op"] = 6, ["d"] = d }, cancellationToken);
        while (true)
        {
            JsonNode message = await Receive(socket, cancellationToken);
            if (message?["op"]?.GetValue<int>() != 7 || message["d"]?["requestId"]?.GetValue<string>() != id)
            {
                continue; // événements d'OBS : ignorés
            }

            JsonNode status = message["d"]!["requestStatus"];
            if (status?["result"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException($"OBS : {status?["comment"]?.GetValue<string>() ?? "requête refusée"}");
            }

            return message["d"]!["responseData"];
        }
    }

    private static Task Send(ClientWebSocket socket, JsonNode message, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<JsonNode> Receive(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var memory = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("OBS a fermé la connexion.");
            }

            memory.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonNode.Parse(memory.ToArray());
    }
}
