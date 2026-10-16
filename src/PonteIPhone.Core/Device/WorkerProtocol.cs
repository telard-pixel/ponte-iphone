using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PonteIPhone.Core.Device;

/// <summary>
/// Messaggi tra l'app e il processo che parla con l'iPhone: una riga JSON per messaggio,
/// su una named pipe privata. I file non passano di qui: il processo li scrive direttamente su disco.
/// </summary>
internal static class WorkerProtocol
{
    public const string OpList = "list";
    public const string OpOpen = "open";
    public const string OpEnumerate = "enum";
    public const string OpThumbnail = "thumb";
    public const string OpDownload = "download";
    public const string OpDelete = "delete";
    public const string OpCancel = "cancel";
    public const string OpStorage = "storage";

    public const string KindOk = "ok";
    public const string KindError = "err";
    public const string KindCanceled = "canceled";
    public const string KindBatch = "batch";
    public const string KindProgress = "progress";
    /// <summary>Il processo comincia a eseguire la richiesta (prima era in coda): da qui conta il watchdog.</summary>
    public const string KindStarted = "started";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
}

internal sealed class WireRequest
{
    public long Id { get; set; }
    public string Op { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? ObjectId { get; set; }
    public string? Destination { get; set; }
    public string[]? Ids { get; set; }
    /// <summary>Per "cancel": la richiesta da interrompere.</summary>
    public long Target { get; set; }
}

internal sealed class WireMessage
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public JsonElement? Data { get; set; }
    public string? Error { get; set; }
    public int Code { get; set; }
}
