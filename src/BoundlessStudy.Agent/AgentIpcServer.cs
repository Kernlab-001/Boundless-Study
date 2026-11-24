using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using BoundlessStudy.Contracts.Ipc;

namespace BoundlessStudy.Agent;

public enum AgentClientRole
{
    Desktop,
    BrowserBridge,
}

/// <summary>
/// Named Pipe IPC server for the per-user background agent (system-architecture.md §7.2; ADR-0005).
/// Listens on BoundlessStudy.UI.&lt;UserSid&gt;, validates connecting client identity, reads length-prefixed
/// JSON frames, and dispatches to <see cref="AgentRequestHandler"/>.
/// </summary>
public sealed class AgentIpcServer : IAsyncDisposable
{
    private readonly AgentRequestHandler _handler;
    private readonly string _pipeName;
    private readonly string? _expectedUserSid;
    private readonly AgentClientRole _clientRole;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerLoop;
    private NamedPipeServerStream? _currentListeningPipe;
    private readonly List<Task> _activeConnections = new();
    private readonly object _connectionLock = new();
    private bool _disposed;

    public AgentIpcServer(
        AgentRequestHandler handler,
        string? pipeName = null,
        string? expectedUserSid = null,
        AgentClientRole clientRole = AgentClientRole.Desktop)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _expectedUserSid = expectedUserSid ?? ResolveCurrentUserSid();
        _pipeName = pipeName ?? ResolveDefaultPipeName(_expectedUserSid);
        _clientRole = clientRole;
    }

    public string PipeName => _pipeName;

    public string? ExpectedUserSid => _expectedUserSid;

    private static string? ResolveCurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveDefaultPipeName(string? userSid)
    {
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            return IpcProtocol.PipeNameForUser(userSid);
        }

        return IpcProtocol.PipeNamePrefix + "Default";
    }

    public void Start()
    {
        if (_listenerLoop is not null) return;
        _listenerLoop = Task.Run(() => RunListenerLoopAsync(_cts.Token));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        await RunListenerLoopAsync(linkedCts.Token);
    }

    private async Task RunListenerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipeServer = null;
            try
            {
                pipeServer = CreatePipeServerStream(_pipeName);
                lock (_connectionLock)
                {
                    _currentListeningPipe = pipeServer;
                }

                using var reg = cancellationToken.Register(() =>
                {
                    try { pipeServer.Dispose(); } catch { }
                });

                await pipeServer.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { pipeServer?.Dispose(); } catch { }
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                try { pipeServer?.Dispose(); } catch { }
                if (cancellationToken.IsCancellationRequested) break;
                Console.Error.WriteLine($"BoundlessStudy.Agent: IPC listener error: {ex.Message}");
                try
                {
                    await Task.Delay(50, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }
            finally
            {
                lock (_connectionLock)
                {
                    if (ReferenceEquals(_currentListeningPipe, pipeServer))
                    {
                        _currentListeningPipe = null;
                    }
                }
            }

            var serverInstance = pipeServer;
            var connectionTask = Task.Run(async () =>
            {
                await HandleConnectionAsync(serverInstance, cancellationToken);
            }, cancellationToken);

            lock (_connectionLock)
            {
                _activeConnections.Add(connectionTask);
                _activeConnections.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
    {
        await using (pipeServer)
        {
            using var reg = cancellationToken.Register(() =>
            {
                try { pipeServer.Dispose(); } catch { }
            });

            bool identityValidated = string.IsNullOrWhiteSpace(_expectedUserSid);

            // Message processing loop
            while (!cancellationToken.IsCancellationRequested)
            {
                IpcRequest? request;
                try
                {
                    request = await IpcFrameCodec.ReadRequestAsync(pipeServer, cancellationToken);
                    if (request is null)
                    {
                        // Normal client disconnection
                        break;
                    }
                }
                catch (InvalidDataException ex)
                {
                    // Malformed frame or JSON error
                    var failResponse = IpcResponse.Fail(
                        Guid.NewGuid().ToString("D"),
                        Guid.NewGuid().ToString("D"),
                        IpcErrors.ProtocolFailure,
                        "Malformed IPC frame or invalid JSON: " + ex.Message);

                    try
                    {
                        await IpcFrameCodec.WriteResponseAsync(pipeServer, failResponse, cancellationToken);
                    }
                    catch { }

                    break;
                }
                catch (Exception)
                {
                    break;
                }

                // Validate client SID on first request (now that client has written data to the pipe)
                if (!identityValidated)
                {
                    bool isAuthorized = false;
                    try
                    {
                        string? clientSid = null;
                        pipeServer.RunAsClient(() =>
                        {
                            clientSid = WindowsIdentity.GetCurrent().User?.Value;
                        });

                        isAuthorized = string.Equals(clientSid, _expectedUserSid, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        isAuthorized = false;
                    }

                    if (!isAuthorized)
                    {
                        var unauthorizedResponse = IpcResponse.Fail(
                            request.RequestId,
                            request.CorrelationId,
                            IpcErrors.UnauthorizedClient,
                            "Client identity is not authorized to connect to this agent pipe.");

                        try
                        {
                            await IpcFrameCodec.WriteResponseAsync(pipeServer, unauthorizedResponse, cancellationToken);
                        }
                        catch { }

                        break;
                    }

                    identityValidated = true;
                }

                IpcResponse response;
                try
                {
                    response = await _handler.HandleRequestAsync(request, cancellationToken, _clientRole);
                }
                catch (Exception ex)
                {
                    response = IpcResponse.Fail(
                        request.RequestId,
                        request.CorrelationId,
                        IpcErrors.OperationFailed,
                        "Internal error processing request: " + ex.Message);
                }

                try
                {
                    await IpcFrameCodec.WriteResponseAsync(pipeServer, response, cancellationToken);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private NamedPipeServerStream CreatePipeServerStream(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        // Fail-closed: restrict the pipe to the expected user SID only.
        // Any client whose SID cannot be verified is rejected at the ACL level
        // before even reaching the per-message identity check in HandleConnectionAsync.
        var security = new PipeSecurity();

        if (!string.IsNullOrWhiteSpace(_expectedUserSid))
        {
            var userSid = new SecurityIdentifier(_expectedUserSid);
            security.AddAccessRule(new PipeAccessRule(
                userSid,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }
        else
        {
            // No SID available — fall back to current user so the pipe is never world-accessible.
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));
            }
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    public async Task StopAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        lock (_connectionLock)
        {
            try
            {
                _currentListeningPipe?.Dispose();
                _currentListeningPipe = null;
            }
            catch { }
        }

        List<Task> connectionsToWait;
        lock (_connectionLock)
        {
            connectionsToWait = new List<Task>(_activeConnections);
            if (_listenerLoop is not null)
            {
                connectionsToWait.Add(_listenerLoop);
            }
        }

        try
        {
            var whenAll = Task.WhenAll(connectionsToWait);
            await Task.WhenAny(whenAll, Task.Delay(1500));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BoundlessStudy.Agent: IPC server shutdown error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
