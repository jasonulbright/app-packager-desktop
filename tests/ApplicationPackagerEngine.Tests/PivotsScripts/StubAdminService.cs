using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>
/// The administration service of CM01 (5.00.9146.1000) as an <see cref="HttpMessageHandler"/>: the
/// bodies are copied from the real answers. A RunCMPivot answer carries "value" for a device list and
/// no wrapper for a collection; a query with an error is a 400 with an empty body; an unknown
/// collection or device is a 404; the status entity set returns no row until a device answers, then
/// one row per device with the result as &lt;result&gt; XML; an offline device and a resource without a
/// client never answer. Time is counted in status reads: a device answers after
/// <see cref="AnswerAfterPolls"/> reads. A device listed in <see cref="Large"/> gets a cut status row
/// (moreResults="True") and its whole result on the device result route.
/// </summary>
internal sealed partial class StubAdminService : HttpMessageHandler
{
    public const string Host = "cm01.contoso.com";

    public const string ServiceDocument = "{\"@odata.context\":\"https://cm01.contoso.com/AdminService/v1.0/$metadata\",\"value\":[{\"name\":\"Application\",\"kind\":\"EntitySet\",\"url\":\"Application\"}]}";

    private const string ScriptParams = "<ScriptParameters><ScriptParameter ParameterGroupGuid=\\\"\\\" ParameterGroupName=\\\"PG_\\\" ParameterName=\\\"kustoquery\\\" ParameterDataType=\\\"System.String\\\" ParameterVisibility=\\\"0\\\" ParameterType=\\\"0\\\" ParameterValue=\\\"E:RSgwKQ==\\\"/></ScriptParameters>";

    private readonly Func<string, IReadOnlyList<(int Id, string Name, bool Client, bool? Online)>> _members;
    private readonly Func<IReadOnlyList<(int Id, string Name, bool Client, bool? Online)>> _devices;
    private readonly Dictionary<long, Operation> _operations = [];
    private long _next = 16784550;

    private sealed class Operation
    {
        public required string Query { get; init; }
        public required List<(int Id, string Name)> Devices { get; init; }
        public required string CollectionId { get; init; }
        public int Reads { get; set; }
    }

    public StubAdminService(Func<string, IReadOnlyList<(int, string, bool, bool?)>> members, Func<IReadOnlyList<(int, string, bool, bool?)>> devices)
    {
        _members = members;
        _devices = devices;
    }

    public List<(string Method, string PathAndQuery, string? Body)> Requests { get; } = [];
    public int AnswerAfterPolls { get; set; } = 2;
    public HashSet<int> Large { get; } = [];
    public HashSet<int> Fails { get; } = [];

    /// <summary>Status reads that answer 503 with the IIS page, as during a restart of SMS_EXECUTIVE.</summary>
    public int Unavailable { get; set; }

    /// <summary>The next send fails with no HTTP answer (a broken connection).</summary>
    public HttpRequestError? BreakNextSend { get; set; }

    /// <summary>The next send waits for this task before it answers.</summary>
    public Func<Task>? BeforeSend { get; set; }

    public int Sends => Requests.Count(r => r.Method == "POST");
    public int StatusReads => Requests.Count(r => r.PathAndQuery.Contains("SMS_CMPivotStatus?", StringComparison.Ordinal));

    [GeneratedRegex(@"^/AdminService/v1\.0/Collections\('([^']*)'\)/AdminService\.RunCMPivot$")]
    private static partial Regex CollectionRun();

    [GeneratedRegex(@"^/AdminService/v1\.0/SMS_CMPivotStatus\?\$filter=ClientOperationId%20eq%20(\d+)(?:%20and%20ResourceId%20ge%20(\d+)&\$orderby=ResourceId&\$top=(\d+))?$")]
    private static partial Regex StatusRead();

    /// <summary>Every status read answers this HTTP status with an empty body, as a site that refuses the identity.</summary>
    public int? StatusAnswer { get; set; }

    /// <summary>The next status reads answer 200 with this body, one per read.</summary>
    public Queue<string> StatusBodies { get; } = new();

    /// <summary>Status reads answer with a body longer than the client reads.</summary>
    public bool StatusTooLarge { get; set; }

    /// <summary>The paged status read is refused with HTTP 400, as a site that does not take $orderby.</summary>
    public bool RefusePaging { get; set; }

    /// <summary>The status reads a device needs before it answers; null uses <see cref="AnswerAfterPolls"/> for every device.</summary>
    public Func<int, int>? AnswerAfter { get; set; }

    /// <summary>The send answers with a body longer than the client reads, after the site created the operation.</summary>
    public bool SendTooLarge { get; set; }

    /// <summary>The ResourceIDs each operation reaches, in the order of the sends.</summary>
    public List<List<int>> SentTo { get; } = [];

    /// <summary>The number of rows of each status answer, in the order of the reads.</summary>
    public List<int> StatusRowCounts { get; } = [];

    /// <summary>A body that says it is longer than <see cref="AdminServiceClient.MaxResponseBytes"/>.</summary>
    private sealed class OversizeContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync("{}"u8.ToArray()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = ApplicationPackagerEngine.Services.AdminServiceClient.MaxResponseBytes + 1L;
            return true;
        }
    }

    [GeneratedRegex(@"^/AdminService/v1\.0/Device\((\d+)\)/AdminService\.CMPivotResult\(OperationId=(\d+)\)$")]
    private static partial Regex DeviceResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal(Host, request.RequestUri!.Host);
        Assert.Equal("https", request.RequestUri.Scheme);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri.PathAndQuery;
        Requests.Add((request.Method.Method, path, body));
        if (request.Method == HttpMethod.Post)
        {
            if (BeforeSend is { } before) { BeforeSend = null; await before(); }
            if (BreakNextSend is { } kind) { BreakNextSend = null; throw new HttpRequestException(kind, "The response ended prematurely."); }
        }
        if (path == "/AdminService/v1.0/") return Json(200, ServiceDocument);
        if (request.Method == HttpMethod.Post)
        {
            using var doc = JsonDocument.Parse(body!);
            var query = doc.RootElement.GetProperty("InputQuery").GetString() ?? "";
            if (query.Length == 0 || query.Contains("wherez", StringComparison.Ordinal) || query.StartsWith("NoSuchEntity", StringComparison.Ordinal)) return Json(400, "");
            List<(int, string)> targets;
            string collection;
            var wrap = true;
            if (CollectionRun().Match(path) is { Success: true } m)
            {
                collection = m.Groups[1].Value;
                var members = _members(collection);
                if (members.Count == 0 && collection.StartsWith("MCM9", StringComparison.Ordinal)) return Json(404, "");
                targets = members.Where(d => d.Client && d.Online != false).Select(d => (d.Id, d.Name)).ToList();
                wrap = false;
            }
            else if (path == "/AdminService/v1.0/SMS_CMPivotStatus/AdminService.RunCMPivot")
            {
                collection = "SMS00001";
                var ids = (doc.RootElement.GetProperty("ResourceIds").GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
                targets = _devices().Where(d => ids.Contains(d.Id) && d.Client && d.Online != false).Select(d => (d.Id, d.Name)).ToList();
            }
            else return Json(404, "");
            var op = _next++;
            _operations[op] = new Operation { Query = query, Devices = targets, CollectionId = collection };
            SentTo.Add(targets.Select(t => t.Item1).OrderBy(i => i).ToList());
            if (SendTooLarge) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new OversizeContent() };
            var inner = $"{{\"ScriptParams\":\"{ScriptParams}\",\"OperationId\":{op},\"Properties\":[{{\"Name\":\"Caption\",\"Units\":0,\"Type\":1,\"Source\":0,\"CalculatedTableOperator\":null}}]}}";
            return Json(200, wrap ? "{\"value\":" + inner + "}" : inner);
        }
        if (StatusRead().Match(path) is { Success: true } s)
        {
            if (Unavailable > 0)
            {
                Unavailable--;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<HTML><HEAD><TITLE>Service Unavailable</TITLE></HEAD><BODY><h2>Service Unavailable</h2><hr><p>HTTP Error 503. The service is unavailable.</p></BODY></HTML>", Encoding.ASCII, "text/html") };
            }
            var op = long.Parse(s.Groups[1].Value);
            var paged = s.Groups[2].Success;
            if (_operations.TryGetValue(op, out var counted)) counted.Reads++;
            if (StatusAnswer is { } refused) return Json(refused, "");
            if (StatusTooLarge) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new OversizeContent() };
            if (StatusBodies.TryDequeue(out var given)) return Json(200, given);
            if (paged && RefusePaging) return Json(400, "{\"error\":{\"code\":\"400\",\"message\":\"The query specified in the URI is not valid.\"}}");
            var from = paged ? int.Parse(s.Groups[2].Value) : int.MinValue;
            var top = paged ? int.Parse(s.Groups[3].Value) : int.MaxValue;
            var rows = new List<string>();
            if (_operations.TryGetValue(op, out var o))
                foreach (var (id, name) in o.Devices.Where(x => x.Id >= from && o.Reads > (AnswerAfter?.Invoke(x.Id) ?? AnswerAfterPolls)).OrderBy(x => x.Id).Take(top))
                    rows.Add(StatusRow(op, o, id, name));
            StatusRowCounts.Add(rows.Count);
            return Json(200, "{\"@odata.context\":\"https://cm01.contoso.com/AdminService/v1.0/$metadata#SMS_CMPivotStatus\",\"value\":[" + string.Join(",", rows) + "]}");
        }
        if (DeviceResult().Match(path) is { Success: true } d)
        {
            var id = int.Parse(d.Groups[1].Value);
            var op = long.Parse(d.Groups[2].Value);
            if (!_operations.TryGetValue(op, out var o) || o.Reads <= AnswerAfterPolls) return Json(404, "");
            var rows = string.Join(",", Enumerable.Range(0, Large.Contains(id) ? 1200 : 1).Select(i => $"{{\"FileName\":\"C:\\\\Windows\\\\System32\\\\file{i}.dll\",\"Size\":\"{26072 + i}\",\"Device\":\"{Name(o, id)}\"}}"));
            return Json(200, "{\"value\":{\"Status\":\"1\",\"MoreResult\":false,\"Result\":[" + rows + "]}}");
        }
        return Json(404, "");
    }

    private static string Name(Operation o, int id) => o.Devices.FirstOrDefault(x => x.Item1 == id).Item2 ?? "";

    private string StatusRow(long op, Operation o, int id, string name)
    {
        string output;
        int state = 1;
        string error = "null";
        if (Fails.Contains(id)) { state = 2; output = ""; error = "\"The query failed: SECRET-DETAIL-1234\""; }
        else if (Large.Contains(id))
            output = "<result ResultCode=\\\"0\\\" moreResults=\\\"True\\\">" + string.Concat(Enumerable.Range(0, 3).Select(i => $"<e _i=\\\"{i}\\\" FileName=\\\"C:\\\\Windows\\\\System32\\\\file{i}.dll\\\" Size=\\\"{26072 + i}\\\" />")) + "</result>";
        else output = "<result ResultCode=\\\"0\\\" moreResults=\\\"False\\\"><e _i=\\\"0\\\" Caption=\\\"Microsoft Windows 11 Enterprise Evaluation\\\" /></result>";
        return $"{{\"ScriptGuid\":\"7DC6B6F1-E7F6-43C1-96E0-E1D16BC25C14\",\"ResourceId\":{id},\"ClientOperationId\":{op},\"TaskID\":\"{{59AE499C-C89F-43DD-BF68-4ACA9DD65D37}}\",\"ScriptVersion\":\"1\",\"ScriptName\":\"CMPivot\",\"ScriptLastModifiedDate\":\"2026-09-08T16:15:50.813Z\",\"SiteNumber\":1,\"DeviceName\":{JsonSerializer.Serialize(name)},\"CollectionId\":\"{o.CollectionId}\",\"LastUpdateTime\":\"2026-10-24T08:29:42.567Z\",\"ScriptExecutionState\":{state},\"ReturnCode\":0,\"ErrorMessage\":{error},\"ScriptExitCode\":0,\"ScriptOutput\":\"{output}\",\"State\":1,\"TotalClients\":0,\"UnknownClients\":0,\"NotApplicableClients\":0,\"FailedClients\":0,\"CompletedClients\":0,\"OfflineClients\":0,\"OutputLength\":116}}";
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
