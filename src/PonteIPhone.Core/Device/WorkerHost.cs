using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Device;

/// <summary>
/// Lato "processo separato": riceve le richieste dall'app e le esegue sul telefono una alla volta.
/// Se il driver si pianta dentro una chiamata, questo processo resta bloccato e l'app lo chiude:
/// è il motivo per cui esiste (vedi <see cref="WorkerConnection"/>).
/// </summary>
public static class WorkerHost
{
    private const int BatchSize = 200;

    /// <summary>
    /// Punto d'ingresso del processo separato ("--worker &lt;pipe&gt; &lt;pid dell'app&gt;").
    /// Si chiude da solo se l'app si chiude o la pipe cade.
    /// </summary>
    public static int RunWorkerProcess(string pipeName, int parentPid, Func<IDeviceBackend> backendFactory)
    {
        using var stop = new CancellationTokenSource();
        try
        {
            var parent = Process.GetProcessById(parentPid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => stop.Cancel();
            if (parent.HasExited) return 0;
        }
        catch (ArgumentException)
        {
            return 0; // l'app non c'è più
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(15_000);
        using var backend = backendFactory();
        Serve(pipe, backend, stop.Token);
        // Un thread del driver bloccato non deve tenere vivo il processo.
        Environment.Exit(0);
        return 0;
    }

    /// <summary>Serve le richieste finché la pipe resta aperta o <paramref name="stop"/> non scatta.</summary>
    public static void Serve(Stream pipe, IDeviceBackend backend, CancellationToken stop)
    {
        var writeLock = new object();
        var writer = new StreamWriter(pipe, WorkerProtocol.Utf8) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(pipe, WorkerProtocol.Utf8);
        var queue = new BlockingCollection<WireRequest>();
        var running = new ConcurrentDictionary<long, CancellationTokenSource>();

        void Send(WireMessage msg)
        {
            var line = JsonSerializer.Serialize(msg, WorkerProtocol.Json);
            lock (writeLock)
            {
                try { writer.WriteLine(line); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }
        }

        var executor = new Thread(() =>
        {
            foreach (var req in queue.GetConsumingEnumerable())
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                running[req.Id] = cts;
                try
                {
                    Send(new WireMessage { Id = req.Id, Kind = WorkerProtocol.KindStarted });
                    Execute(req, backend, Send, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Send(new WireMessage { Id = req.Id, Kind = WorkerProtocol.KindCanceled });
                }
                catch (DeviceException e)
                {
                    Send(new WireMessage { Id = req.Id, Kind = WorkerProtocol.KindError, Error = e.Message, Code = e.Code });
                }
                catch (Exception e)
                {
                    Send(new WireMessage { Id = req.Id, Kind = WorkerProtocol.KindError, Error = e.Message, Code = e.HResult });
                }
                finally
                {
                    running.TryRemove(req.Id, out _);
                    cts.Dispose();
                }
            }
        }) { IsBackground = true, Name = "iPhone" };
        if (OperatingSystem.IsWindows()) executor.SetApartmentState(ApartmentState.MTA);
        executor.Start();

        using var stopReg = stop.Register(() => { try { pipe.Dispose(); } catch { } });
        try
        {
            while (!stop.IsCancellationRequested)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { break; }
                if (line is null) break;
                WireRequest? req;
                try { req = JsonSerializer.Deserialize<WireRequest>(line, WorkerProtocol.Json); }
                catch (JsonException) { continue; }
                if (req is null) continue;

                if (req.Op == WorkerProtocol.OpCancel)
                {
                    if (running.TryGetValue(req.Target, out var c)) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
                    continue;
                }
                queue.Add(req);
            }
        }
        finally
        {
            queue.CompleteAdding();
            foreach (var c in running.Values) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
            // Si aspetta poco: se il driver è bloccato, il thread resta lì e il processo esce comunque.
            executor.Join(TimeSpan.FromSeconds(3));
        }
    }

    private static void Execute(WireRequest req, IDeviceBackend backend, Action<WireMessage> send, CancellationToken ct)
    {
        WireMessage Ok(object? data) => new()
        {
            Id = req.Id,
            Kind = WorkerProtocol.KindOk,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data, WorkerProtocol.Json),
        };

        switch (req.Op)
        {
            case WorkerProtocol.OpList:
                send(Ok(backend.ListDevices()));
                break;
            case WorkerProtocol.OpOpen:
                send(Ok(backend.Open(req.DeviceId ?? "")));
                break;
            case WorkerProtocol.OpEnumerate:
            {
                long total = 0;
                backend.Enumerate(batch =>
                {
                    ct.ThrowIfCancellationRequested();
                    // Blocchi piccoli: ognuno è anche un segno di vita per il watchdog.
                    for (int i = 0; i < batch.Count; i += BatchSize)
                    {
                        var part = batch.Skip(i).Take(BatchSize).ToList();
                        total += part.Count;
                        send(new WireMessage
                        {
                            Id = req.Id,
                            Kind = WorkerProtocol.KindBatch,
                            Data = JsonSerializer.SerializeToElement(part, WorkerProtocol.Json),
                        });
                    }
                    if (batch.Count == 0)
                        send(new WireMessage { Id = req.Id, Kind = WorkerProtocol.KindProgress });
                }, ct);
                send(Ok(new EnumerateSummary(total, backend.CloudOnlyFiles, backend.LastEnumerationSummary)));
                break;
            }
            case WorkerProtocol.OpThumbnail:
                send(Ok(backend.GetThumbnail(req.ObjectId ?? "")));
                break;
            case WorkerProtocol.OpDownload:
            {
                long lastSent = -1;
                var sw = Stopwatch.StartNew();
                var result = backend.Download(req.ObjectId ?? "", req.Destination ?? throw new ArgumentException("destinazione"),
                    bytes =>
                    {
                        // Al massimo 4 messaggi al secondo: bastano per la barra e per il watchdog.
                        if (bytes != lastSent && sw.ElapsedMilliseconds >= 250)
                        {
                            lastSent = bytes;
                            sw.Restart();
                            send(new WireMessage
                            {
                                Id = req.Id,
                                Kind = WorkerProtocol.KindProgress,
                                Data = JsonSerializer.SerializeToElement(bytes, WorkerProtocol.Json),
                            });
                        }
                    }, ct);
                send(Ok(result));
                break;
            }
            case WorkerProtocol.OpDelete:
                send(Ok(backend.Delete(req.Ids ?? [])));
                break;
            default:
                send(new WireMessage { Id = req.Id, Kind = WorkerProtocol.KindError, Error = "Operazione sconosciuta: " + req.Op });
                break;
        }
    }
}
