using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Wallpaper.Core;

namespace Wallpaper.Player;

/// <summary>
/// mpv 的 JSON IPC 通道（命名管道）。用于暂停/继续、音量、读取时长等实时控制，
/// 避免"改一个参数就重启进程"。
/// </summary>
public sealed class MpvIpc : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId = 1;

    /// <summary>mpv 的 property-change 事件（如 duration / pause）。</summary>
    public event Action<string, JsonElement?>? PropertyChanged;

    private MpvIpc(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        _ = Task.Run(ReadLoopAsync);
    }

    public static string BuildPipePath(string name)
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        return sep + sep + "." + sep + "pipe" + sep + name;
    }

    public static MpvIpc? Connect(string pipeName, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(400);
                Log.Info("mpv ipc connected: " + pipeName);
                return new MpvIpc(pipe);
            }
            catch (Exception)
            {
                Thread.Sleep(120);
            }
        }

        Log.Warn("mpv ipc connect timeout: " + pipeName);
        return null;
    }

    public async Task<JsonElement?> GetPropertyAsync(string name, int timeoutMs = 2000)
    {
        try
        {
            return await SendAsync(new object[] { "get_property", name }, timeoutMs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("get_property " + name + " failed: " + ex.Message);
            return null;
        }
    }

    public void SetProperty(string name, object value)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(new object[] { "set_property", name, value }, 2000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("set_property " + name + " failed: " + ex.Message);
            }
        });
    }

    public void Command(params object[] args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(args, 2000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("mpv command failed: " + ex.Message);
            }
        });
    }

    /// <summary>让 mpv 主动推送某个属性的变化。</summary>
    public void Observe(string name, int id)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(new object[] { "observe_property", id, name }, 2000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("observe_property " + name + " failed: " + ex.Message);
            }
        });
    }

    private async Task<JsonElement?> SendAsync(object[] command, int timeoutMs)
    {
        int id;
        TaskCompletionSource<JsonElement?> tcs;

        lock (_gate)
        {
            id = _nextId++;
            tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["request_id"] = id,
        });

        // 多个命令可能并发下发（observe / set_property / get_property），
        // StreamWriter 不允许并发写，否则报 "stream is currently in use"
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(payload).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            lock (_gate) _pending.Remove(id);
            return null;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("event", out var evt))
                {
                    if (evt.GetString() == "property-change" &&
                        root.TryGetProperty("name", out var nameProp))
                    {
                        JsonElement? data = root.TryGetProperty("data", out var d) ? d.Clone() : null;
                        PropertyChanged?.Invoke(nameProp.GetString() ?? "", data);
                    }

                    continue;
                }

                if (root.TryGetProperty("request_id", out var rid) && rid.TryGetInt32(out var id))
                {
                    TaskCompletionSource<JsonElement?>? tcs;
                    lock (_gate)
                    {
                        if (!_pending.Remove(id, out tcs)) tcs = null;
                    }

                    JsonElement? result = root.TryGetProperty("data", out var data) ? data.Clone() : null;
                    tcs?.TrySetResult(result);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("mpv ipc read loop ended: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _writeLock.Dispose();
            _writer.Dispose();
            _reader.Dispose();
            _pipe.Dispose();
        }
        catch
        {
            // 关闭阶段的异常忽略
        }
    }
}
