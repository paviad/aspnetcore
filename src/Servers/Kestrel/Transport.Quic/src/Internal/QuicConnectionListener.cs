// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Quic.Internal;

/// <summary>
/// Listens for new Quic Connections.
/// </summary>
internal sealed class QuicConnectionListener : IMultiplexedConnectionListener, IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly TlsConnectionCallbackOptions _tlsConnectionCallbackOptions;
    private readonly QuicTransportContext _context;
    private readonly QuicListenerOptions _quicListenerOptions;
    // Use a CWT to associate QuicConnectionContext with QuicConnection in the callback because there are some situations
    // where the QuicConnection won't be returned and we can't manually remove the item. e.g. invalid connection options.
    // Internal for unit testing.
    internal readonly ConditionalWeakTable<QuicConnection, QuicConnectionContext> _pendingConnections;
    private bool _disposed;
    private QuicListener? _listener;

    public QuicConnectionListener(
        QuicTransportOptions options,
        ILogger log,
        EndPoint endpoint,
        TlsConnectionCallbackOptions tlsConnectionCallbackOptions)
    {
        if (!QuicListener.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported or enabled on this platform. See https://aka.ms/aspnet/kestrel/http3reqs for details.");
        }

        if (endpoint is not IPEndPoint listenEndPoint)
        {
            throw new InvalidOperationException($"QUIC doesn't support listening on the configured endpoint type. Expected {nameof(IPEndPoint)} but got {endpoint.GetType().Name}.");
        }

        if (tlsConnectionCallbackOptions.ApplicationProtocols.Count == 0)
        {
            throw new InvalidOperationException("No application protocols specified.");
        }

        _pendingConnections = new ConditionalWeakTable<QuicConnection, QuicConnectionContext>();
        _log = log;
        _tlsConnectionCallbackOptions = tlsConnectionCallbackOptions;
        _context = new QuicTransportContext(_log, options);
        _quicListenerOptions = new QuicListenerOptions
        {
            ApplicationProtocols = _tlsConnectionCallbackOptions.ApplicationProtocols,
            ListenEndPoint = listenEndPoint,
            ListenBacklog = options.Backlog,
            ConnectionOptionsCallback = async (connection, helloInfo, cancellationToken) =>
            {
                // Create the connection context inside the callback because it's passed
                // to the connection callback. The field is then read once AcceptConnectionAsync
                // finishes awaiting.
                var currentAcceptingConnection = new QuicConnectionContext(connection, _context);
                _pendingConnections.Add(connection, currentAcceptingConnection);

                var context = new TlsConnectionCallbackContext
                {
                    ClientHelloInfo = helloInfo,
                    State = _tlsConnectionCallbackOptions.OnConnectionState,
                    Connection = currentAcceptingConnection,
                };
                var serverAuthenticationOptions = await _tlsConnectionCallbackOptions.OnConnection(context, cancellationToken);

                // If the callback didn't set protocols then use the listener's list of protocols.
                if (serverAuthenticationOptions.ApplicationProtocols == null)
                {
                    serverAuthenticationOptions.ApplicationProtocols = _tlsConnectionCallbackOptions.ApplicationProtocols;
                }

                // If the SslServerAuthenticationOptions doesn't have a cert or protocols then the
                // QUIC connection will fail and the client receives an unhelpful message.
                // Validate the options on the server and log issues to improve debugging.
                ValidateServerAuthenticationOptions(serverAuthenticationOptions);

                var connectionOptions = new QuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = serverAuthenticationOptions,
                    IdleTimeout = options.IdleTimeout,
                    MaxInboundBidirectionalStreams = options.MaxBidirectionalStreamCount,
                    MaxInboundUnidirectionalStreams = options.MaxUnidirectionalStreamCount,
                    DefaultCloseErrorCode = options.DefaultCloseErrorCode,
                    DefaultStreamErrorCode = options.DefaultStreamErrorCode,
                };
                return connectionOptions;
            }
        };

        // Setting to listenEndPoint to prevent the property from being null.
        // This will be initialized when CreateListenerAsync() is invoked.
        EndPoint = listenEndPoint;
    }

    private void ValidateServerAuthenticationOptions(SslServerAuthenticationOptions serverAuthenticationOptions)
    {
        if (serverAuthenticationOptions.ServerCertificate == null &&
            serverAuthenticationOptions.ServerCertificateContext == null &&
            serverAuthenticationOptions.ServerCertificateSelectionCallback == null)
        {
            QuicLog.ConnectionListenerCertificateNotSpecified(_log);
        }
        if (serverAuthenticationOptions.ApplicationProtocols == null || serverAuthenticationOptions.ApplicationProtocols.Count == 0)
        {
            QuicLog.ConnectionListenerApplicationProtocolsNotSpecified(_log);
        }
    }

    public EndPoint EndPoint { get; set; }

    public async ValueTask CreateListenerAsync()
    {
        QuicLog.ConnectionListenerStarting(_log, _quicListenerOptions.ListenEndPoint);

        try
        {
            _listener = await QuicListener.ListenAsync(_quicListenerOptions);
        }
        catch (QuicException ex) when (ex.QuicError == QuicError.AddressInUse)
        {
            throw new AddressInUseException(ex.Message, ex);
        }

        // EndPoint could be configured with an ephemeral port of 0.
        // Listener endpoint will resolve an ephemeral port, e.g. 127.0.0.1:0, into the actual port
        // so we need to update the public listener endpoint property.
        EndPoint = _listener.LocalEndPoint;
    }

    public async ValueTask<MultiplexedConnectionContext?> AcceptAsync(IFeatureCollection? features = null, CancellationToken cancellationToken = default)
    {
        if (_listener == null)
        {
            throw new InvalidOperationException($"The listener needs to be initialized by calling {nameof(CreateListenerAsync)}.");
        }

        try
        {
            var quicConnection = await _listener.AcceptConnectionAsync(cancellationToken);

            if (!_pendingConnections.TryGetValue(quicConnection, out var connectionContext))
            {
                throw new InvalidOperationException("Couldn't find ConnectionContext for QuicConnection.");
            }
            else
            {
                _pendingConnections.Remove(quicConnection);
            }

            // Verify the connection context was created and set correctly.
            Debug.Assert(connectionContext != null);
            Debug.Assert(connectionContext.GetInnerConnection() == quicConnection);

            QuicLog.AcceptedConnection(_log, connectionContext);

            return connectionContext;
        }
        catch (QuicException ex) when (ex.QuicError == QuicError.OperationAborted)
        {
            QuicLog.ConnectionListenerAborted(_log, ex);
        }
        return null;
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_listener != null)
        {
            await _listener.DisposeAsync();
        }
        _disposed = true;
    }
}
